using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentGateway.Data;
using PaymentGateway.Domain.Cards;
using PaymentGateway.Domain.Entities;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services.Acquiring;
using PaymentGateway.Services.Dtos;
using PaymentGateway.Services.Webhooks;

namespace PaymentGateway.Services;

public interface IPaymentService
{
    Task<(PaymentResponse Payment, bool Created)> AuthorizeAsync(
        Guid merchantId, CreatePaymentRequest request, string? idempotencyKey, CancellationToken ct = default);

    Task<PaymentResponse> CaptureAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default);
    Task<PaymentResponse> VoidAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default);
    Task<PaymentResponse> RefundAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default);
    Task<PaymentResponse> GetAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default);

    Task<PagedResponse<PaymentResponse>> ListAsync(
        Guid merchantId, int page, int pageSize, PaymentStatus? status, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentEventResponse>> GetEventsAsync(
        Guid merchantId, Guid paymentId, CancellationToken ct = default);
}

public class PaymentService : IPaymentService
{
    private readonly AppDbContext _db;
    private readonly IWebhookQueue _webhookQueue;
    private readonly GatewayOptions _options;

    public PaymentService(AppDbContext db, IWebhookQueue webhookQueue, IOptions<GatewayOptions> options)
    {
        _db = db;
        _webhookQueue = webhookQueue;
        _options = options.Value;
    }

    public async Task<(PaymentResponse Payment, bool Created)> AuthorizeAsync(
        Guid merchantId, CreatePaymentRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        // 1. Idempotência: mesma chave => devolve o pagamento já criado,
        // sem cobrar o cliente de novo.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await FindByIdempotencyKeyAsync(merchantId, idempotencyKey, ct);
            if (existing is not null)
                return (PaymentResponse.FromEntity(existing), false);
        }

        // 2. Validações do cartão feitas pelo próprio gateway.
        if (!CardValidator.IsValidLuhn(request.Card.Number))
            throw new DomainException("Número de cartão inválido (falhou na verificação de Luhn).");

        var last4 = CardValidator.GetLast4(request.Card.Number);
        var brand = CardValidator.DetectBrand(request.Card.Number);

        Payment payment;

        if (CardValidator.IsExpired(request.Card.ExpMonth, request.Card.ExpYear, DateTime.UtcNow))
        {
            payment = Payment.Decline(
                merchantId, request.AmountInCents, request.Currency.ToUpperInvariant(),
                last4, brand, request.Description, "expired_card");
        }
        else
        {
            // 3. Autorização junto ao "emissor" (simulado).
            var declineReason = AcquirerSimulator.Authorize(request.Card.Number);

            payment = declineReason is null
                ? Payment.Authorize(
                    merchantId, request.AmountInCents, request.Currency.ToUpperInvariant(),
                    last4, brand, request.Description)
                : Payment.Decline(
                    merchantId, request.AmountInCents, request.Currency.ToUpperInvariant(),
                    last4, brand, request.Description, declineReason);
        }

        _db.Payments.Add(payment);

        var eventType = payment.Status == PaymentStatus.Authorized
            ? "payment.authorized"
            : "payment.declined";
        var paymentEvent = RecordEvent(payment, eventType);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            _db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                MerchantId = merchantId,
                IdempotencyKey = idempotencyKey,
                PaymentId = payment.Id
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Duas requisições com a mesma chave chegaram ao mesmo tempo e o
            // índice único barrou a segunda: devolvemos o pagamento vencedor.
            _db.ChangeTracker.Clear();
            var winner = await FindByIdempotencyKeyAsync(merchantId, idempotencyKey!, ct)
                ?? throw new DomainException("Conflito de idempotência. Tente novamente.");
            return (PaymentResponse.FromEntity(winner), false);
        }

        _webhookQueue.Enqueue(paymentEvent.Id);
        return (PaymentResponse.FromEntity(payment), true);
    }

    public async Task<PaymentResponse> CaptureAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);

        payment.Capture();

        // Partidas dobradas: débito total = créditos (repasse + taxa).
        var fee = payment.AmountInCents * _options.FeeBps / 10_000;
        var net = payment.AmountInCents - fee;

        _db.LedgerEntries.AddRange(
            NewEntry(payment, LedgerAccount.AcquirerCash, LedgerEntryType.Debit,
                payment.AmountInCents, "Captura do pagamento"),
            NewEntry(payment, LedgerAccount.MerchantPayable, LedgerEntryType.Credit,
                net, "Repasse ao lojista (valor - taxa)"),
            NewEntry(payment, LedgerAccount.AcquirerRevenue, LedgerEntryType.Credit,
                fee, $"Taxa do gateway ({_options.FeeBps} bps)"));

        var paymentEvent = RecordEvent(payment, "payment.captured");
        await _db.SaveChangesAsync(ct);

        _webhookQueue.Enqueue(paymentEvent.Id);
        return PaymentResponse.FromEntity(payment);
    }

    public async Task<PaymentResponse> VoidAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);

        // Void acontece antes da captura, então não há lançamentos a reverter.
        payment.Void();

        var paymentEvent = RecordEvent(payment, "payment.voided");
        await _db.SaveChangesAsync(ct);

        _webhookQueue.Enqueue(paymentEvent.Id);
        return PaymentResponse.FromEntity(payment);
    }

    public async Task<PaymentResponse> RefundAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);

        payment.Refund();

        // Estorno reverte os lançamentos da captura: o razão nunca é editado
        // nem apagado, apenas recebe os movimentos contrários.
        var fee = payment.AmountInCents * _options.FeeBps / 10_000;
        var net = payment.AmountInCents - fee;

        _db.LedgerEntries.AddRange(
            NewEntry(payment, LedgerAccount.AcquirerCash, LedgerEntryType.Credit,
                payment.AmountInCents, "Estorno do pagamento"),
            NewEntry(payment, LedgerAccount.MerchantPayable, LedgerEntryType.Debit,
                net, "Estorno do repasse ao lojista"),
            NewEntry(payment, LedgerAccount.AcquirerRevenue, LedgerEntryType.Debit,
                fee, "Estorno da taxa do gateway"));

        var paymentEvent = RecordEvent(payment, "payment.refunded");
        await _db.SaveChangesAsync(ct);

        _webhookQueue.Enqueue(paymentEvent.Id);
        return PaymentResponse.FromEntity(payment);
    }

    public async Task<PaymentResponse> GetAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);
        return PaymentResponse.FromEntity(payment);
    }

    public async Task<PagedResponse<PaymentResponse>> ListAsync(
        Guid merchantId, int page, int pageSize, PaymentStatus? status, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Payments.AsNoTracking().Where(p => p.MerchantId == merchantId);

        if (status is not null)
            query = query.Where(p => p.Status == status);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => PaymentResponse.FromEntity(p))
            .ToListAsync(ct);

        return new PagedResponse<PaymentResponse>(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<PaymentEventResponse>> GetEventsAsync(
        Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        await LoadPaymentAsync(merchantId, paymentId, ct);

        return await _db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentId == paymentId)
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => PaymentEventResponse.FromEntity(e))
            .ToListAsync(ct);
    }

    private async Task<Payment?> FindByIdempotencyKeyAsync(
        Guid merchantId, string idempotencyKey, CancellationToken ct)
    {
        var record = await _db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MerchantId == merchantId && r.IdempotencyKey == idempotencyKey, ct);

        if (record is null)
            return null;

        return await _db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == record.PaymentId, ct);
    }

    private async Task<Payment> LoadPaymentAsync(Guid merchantId, Guid paymentId, CancellationToken ct)
    {
        // Filtrar por MerchantId garante isolamento entre lojistas: ninguém
        // enxerga (nem manipula) pagamento de outro, mesmo sabendo o ID.
        var payment = await _db.Payments
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.MerchantId == merchantId, ct);

        return payment ?? throw new NotFoundException($"Pagamento {paymentId} não encontrado.");
    }

    private PaymentEvent RecordEvent(Payment payment, string type)
    {
        var paymentEvent = new PaymentEvent
        {
            MerchantId = payment.MerchantId,
            PaymentId = payment.Id,
            Type = type,
            PayloadJson = JsonSerializer.Serialize(PaymentResponse.FromEntity(payment))
        };

        _db.PaymentEvents.Add(paymentEvent);
        return paymentEvent;
    }

    private static LedgerEntry NewEntry(
        Payment payment, LedgerAccount account, LedgerEntryType type, long amountInCents, string description) =>
        new()
        {
            MerchantId = payment.MerchantId,
            PaymentId = payment.Id,
            Account = account,
            Type = type,
            AmountInCents = amountInCents,
            Description = description
        };
}
