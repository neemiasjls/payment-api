using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentGateway.Data;
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

    Task<PaymentResponse> CaptureAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey = null, CancellationToken ct = default);
    Task<PaymentResponse> VoidAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey = null, CancellationToken ct = default);
    Task<PaymentResponse> RefundAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey = null, CancellationToken ct = default);
    Task<PaymentResponse> GetAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default);
    Task<PaymentResponse> RefreshAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default);
    Task<PagedResponse<PaymentResponse>> ListAsync(
        Guid merchantId, int page, int pageSize, PaymentStatus? status, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentEventResponse>> GetEventsAsync(
        Guid merchantId, Guid paymentId, CancellationToken ct = default);
    Task ReconcileProviderEventAsync(
        string eventId, string providerPaymentId, Guid? localPaymentId, CancellationToken ct = default);
}

public sealed class PaymentService : IPaymentService
{
    private readonly AppDbContext _db;
    private readonly IWebhookQueue _webhookQueue;
    private readonly GatewayOptions _options;
    private readonly IPaymentProcessor _processor;

    public PaymentService(
        AppDbContext db, IWebhookQueue webhookQueue, IOptions<GatewayOptions> options,
        IPaymentProcessor processor)
    {
        _db = db;
        _webhookQueue = webhookQueue;
        _options = options.Value;
        _processor = processor;
        if (_options.FeeBps is < 0 or > 10_000)
            throw new InvalidOperationException("Gateway:FeeBps deve estar entre 0 e 10000.");
    }

    public async Task<(PaymentResponse Payment, bool Created)> AuthorizeAsync(
        Guid merchantId, CreatePaymentRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        TestPaymentMethods.GetCard(request.PaymentMethodId);
        if (request.AmountInCents is < 50 or > 99_999_999)
            throw new DomainException("O valor deve estar entre 50 e 99.999.999 centavos.");
        if (!string.Equals(request.Currency, "BRL", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("A demonstração aceita apenas BRL.");

        var currency = request.Currency.ToUpperInvariant();
        var requestHash = Hash(JsonSerializer.Serialize(new
        {
            request.AmountInCents, Currency = currency, request.Description, request.PaymentMethodId
        }));
        ValidateKey(idempotencyKey);

        Payment payment;
        var created = true;
        var existingRecord = await FindKeyAsync(merchantId, idempotencyKey, ct);

        if (existingRecord is not null)
        {
            EnsureKeyMatches(existingRecord, "authorize", requestHash);
            payment = await LoadPaymentAsync(merchantId, existingRecord.PaymentId, ct);
            created = false;
        }
        else
        {
            payment = Payment.CreatePending(
                merchantId, request.AmountInCents, currency, request.Description, _processor.Name);
            _db.Payments.Add(payment);

            if (idempotencyKey is not null)
                _db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    MerchantId = merchantId,
                    IdempotencyKey = idempotencyKey,
                    Operation = "authorize",
                    RequestHash = requestHash,
                    PaymentId = payment.Id
                });

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (idempotencyKey is not null)
            {
                _db.ChangeTracker.Clear();
                var winner = await FindKeyAsync(merchantId, idempotencyKey, ct);
                if (winner is null)
                    throw;
                EnsureKeyMatches(winner, "authorize", requestHash);
                payment = await LoadPaymentAsync(merchantId, winner.PaymentId, ct);
                created = false;
            }
        }

        if (payment.Status is not (PaymentStatus.Pending or PaymentStatus.RequiresAction))
            return (PaymentResponse.FromEntity(payment), created);

        EnsureConfiguredProvider(payment);
        // A Stripe pode expirar a chave idempotente após 24h. Sem o ID remoto,
        // uma nova tentativa tardia poderia criar outra cobrança de teste.
        if (!created && _processor.Name == "stripe" && payment.ProviderPaymentId is null &&
            DateTime.UtcNow - payment.CreatedAtUtc >= TimeSpan.FromHours(23))
            throw new DomainException(
                "Autorização pendente há mais de 23 horas sem ID Stripe. Aguarde o webhook ou reconcilie manualmente; não repita a criação.");

        var result = payment.ProviderPaymentId is null
            ? await _processor.AuthorizeAsync(new ProviderAuthorizeRequest(
                payment.Id, payment.AmountInCents, payment.Currency, request.PaymentMethodId,
                payment.Description, OperationKey(payment.Id, "authorize")), ct)
            : await _processor.GetAsync(payment.ProviderPaymentId, ct);

        var response = await ApplyAndPersistAsync(payment, result, null, null, ct);
        return (response, created);
    }

    public Task<PaymentResponse> CaptureAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey = null, CancellationToken ct = default) =>
        ExecuteOperationAsync(merchantId, paymentId, "capture", idempotencyKey, ct);

    public Task<PaymentResponse> VoidAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey = null, CancellationToken ct = default) =>
        ExecuteOperationAsync(merchantId, paymentId, "void", idempotencyKey, ct);

    public Task<PaymentResponse> RefundAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey = null, CancellationToken ct = default) =>
        ExecuteOperationAsync(merchantId, paymentId, "refund", idempotencyKey, ct);

    private async Task<PaymentResponse> ExecuteOperationAsync(
        Guid merchantId, Guid paymentId, string operation, string? idempotencyKey, CancellationToken ct)
    {
        if (!await _db.Payments.AsNoTracking().AnyAsync(
            p => p.Id == paymentId && p.MerchantId == merchantId, ct))
            throw new NotFoundException($"Pagamento {paymentId} não encontrado.");
        await ValidateAndStoreOperationKeyAsync(merchantId, paymentId, operation, idempotencyKey, ct);
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);
        EnsureConfiguredProvider(payment);

        if (operation switch
        {
            "capture" => payment.Status == PaymentStatus.Captured,
            "void" => payment.Status == PaymentStatus.Voided,
            "refund" => payment.Status == PaymentStatus.Refunded,
            _ => false
        })
            return PaymentResponse.FromEntity(payment);

        if (payment.PendingOperation is not null && payment.PendingOperation != operation)
            throw new DomainException($"A operação {payment.PendingOperation} ainda está pendente.");

        var expectedStatus = operation == "refund" ? PaymentStatus.Captured : PaymentStatus.Authorized;
        if (payment.Status != expectedStatus)
            throw new DomainException($"Não é possível executar {operation} em {payment.Status}.");
        if (payment.ProviderPaymentId is null)
            throw new DomainException("Pagamento sem identificador do provedor.");

        // Persiste a intenção antes da chamada externa. Se a resposta HTTP se
        // perder, GET e o webhook ainda sabem qual operação reconciliar.
        if (payment.PendingOperation is null)
        {
            payment.MarkOperationPending(operation);
            var pendingEvent = RecordEvent(payment, $"payment.{operation}_pending");
            try
            {
                await _db.SaveChangesAsync(ct);
                _webhookQueue.Enqueue(pendingEvent.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                payment = await LoadPaymentAsync(merchantId, paymentId, ct);
                if (operation switch
                {
                    "capture" => payment.Status == PaymentStatus.Captured,
                    "void" => payment.Status == PaymentStatus.Voided,
                    "refund" => payment.Status == PaymentStatus.Refunded,
                    _ => false
                })
                    return PaymentResponse.FromEntity(payment);
                if (payment.PendingOperation != operation)
                    throw new DomainException("Outra operação alterou este pagamento. Consulte o estado atual.");
            }
        }

        var providerPaymentId = payment.ProviderPaymentId
            ?? throw new DomainException("Pagamento sem identificador do provedor.");
        var result = operation switch
        {
            "capture" => await _processor.CaptureAsync(
                providerPaymentId, OperationKey(payment.Id, operation), ct),
            "void" => await _processor.VoidAsync(
                providerPaymentId, OperationKey(payment.Id, operation), ct),
            "refund" => await _processor.RefundAsync(
                providerPaymentId, OperationKey(payment.Id, operation), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        return await ApplyAndPersistAsync(payment, result, operation, null, ct);
    }

    public async Task<PaymentResponse> GetAsync(Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);
        if ((payment.Status is PaymentStatus.Pending or PaymentStatus.RequiresAction ||
             payment.PendingOperation is not null) && payment.ProviderPaymentId is not null)
        {
            EnsureConfiguredProvider(payment);
            var result = await _processor.GetAsync(payment.ProviderPaymentId, ct);
            return await ApplyAndPersistAsync(payment, result, payment.PendingOperation, null, ct);
        }

        return PaymentResponse.FromEntity(payment);
    }

    public async Task<PaymentResponse> RefreshAsync(
        Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        var payment = await LoadPaymentAsync(merchantId, paymentId, ct);
        EnsureConfiguredProvider(payment);
        if (payment.ProviderPaymentId is null)
            return PaymentResponse.FromEntity(payment);
        var result = await _processor.GetAsync(payment.ProviderPaymentId, ct);
        return await ApplyAndPersistAsync(payment, result, payment.PendingOperation, null, ct);
    }

    public async Task ReconcileProviderEventAsync(
        string eventId, string providerPaymentId, Guid? localPaymentId, CancellationToken ct = default)
    {
        if (await _db.ProviderWebhookEvents.AsNoTracking().AnyAsync(
            e => e.Provider == _processor.Name && e.EventId == eventId, ct))
            return;

        var payment = await _db.Payments.FirstOrDefaultAsync(
            p => p.Provider == _processor.Name && p.ProviderPaymentId == providerPaymentId, ct);
        if (payment is null && localPaymentId is not null)
            payment = await _db.Payments.FirstOrDefaultAsync(
                p => p.Id == localPaymentId && p.Provider == _processor.Name, ct);
        if (payment is null)
            return; // O evento pode pertencer a outra integração da mesma conta de testes.

        var result = await _processor.GetAsync(providerPaymentId, ct);
        await ApplyAndPersistAsync(payment, result, payment.PendingOperation, eventId, ct);
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
        var items = await query.OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => PaymentResponse.FromEntity(p)).ToListAsync(ct);
        return new PagedResponse<PaymentResponse>(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<PaymentEventResponse>> GetEventsAsync(
        Guid merchantId, Guid paymentId, CancellationToken ct = default)
    {
        await LoadPaymentAsync(merchantId, paymentId, ct);
        return await _db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentId == paymentId)
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => PaymentEventResponse.FromEntity(e)).ToListAsync(ct);
    }

    private async Task<PaymentResponse> ApplyAndPersistAsync(
        Payment payment, ProviderPaymentResult result, string? operation,
        string? providerEventId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ValidateProviderResult(payment, result);
            var paymentEvent = ApplyProviderResult(payment, result, operation);

            if (providerEventId is not null)
                _db.ProviderWebhookEvents.Add(new ProviderWebhookEvent
                {
                    Provider = _processor.Name,
                    EventId = providerEventId,
                    ProviderPaymentId = result.ProviderPaymentId
                });

            if (paymentEvent is null && providerEventId is null)
                return PaymentResponse.FromEntity(payment, result.ClientSecret);

            try
            {
                await _db.SaveChangesAsync(ct);
                if (paymentEvent is not null)
                    _webhookQueue.Enqueue(paymentEvent.Id);
                return PaymentResponse.FromEntity(payment, result.ClientSecret);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                _db.ChangeTracker.Clear();
                payment = await LoadPaymentAsync(payment.MerchantId, payment.Id, ct);
            }
            catch (DbUpdateException) when (providerEventId is not null)
            {
                _db.ChangeTracker.Clear();
                if (!await _db.ProviderWebhookEvents.AsNoTracking().AnyAsync(
                    e => e.Provider == _processor.Name && e.EventId == providerEventId, ct))
                    throw;
                var current = await LoadPaymentAsync(payment.MerchantId, payment.Id, ct);
                return PaymentResponse.FromEntity(current, result.ClientSecret);
            }
        }

        throw new DbUpdateConcurrencyException("Não foi possível reconciliar o pagamento após tentativas concorrentes.");
    }

    private PaymentEvent? ApplyProviderResult(
        Payment payment, ProviderPaymentResult result, string? operation)
    {
        var oldVersion = payment.Version;
        var priorPendingOperation = payment.PendingOperation;
        var newStatus = result.Status switch
        {
            ProviderPaymentStatus.Authorized => PaymentStatus.Authorized,
            ProviderPaymentStatus.Captured => PaymentStatus.Captured,
            ProviderPaymentStatus.Voided => PaymentStatus.Voided,
            ProviderPaymentStatus.Refunded => PaymentStatus.Refunded,
            ProviderPaymentStatus.Declined => PaymentStatus.Declined,
            ProviderPaymentStatus.RequiresAction => PaymentStatus.RequiresAction,
            _ => PaymentStatus.Pending
        };

        if (payment.Status is PaymentStatus.Pending or PaymentStatus.RequiresAction)
        {
            if (newStatus is PaymentStatus.Pending or PaymentStatus.RequiresAction or
                PaymentStatus.Authorized or PaymentStatus.Declined &&
                (payment.Status != newStatus || payment.ProviderPaymentId != result.ProviderPaymentId ||
                 payment.DeclineReason != result.DeclineReason))
                payment.ApplyAuthorization(result.ProviderPaymentId, newStatus,
                    result.CardLast4, result.CardBrand, result.DeclineReason);
            else if (newStatus is PaymentStatus.Captured or PaymentStatus.Voided or PaymentStatus.Refunded)
            {
                payment.ApplyAuthorization(result.ProviderPaymentId, PaymentStatus.Authorized,
                    result.CardLast4, result.CardBrand, null);
                if (newStatus == PaymentStatus.Voided)
                    payment.Void();
                else
                {
                    payment.Capture();
                    AddCaptureEntries(payment);
                    if (newStatus == PaymentStatus.Refunded)
                    {
                        payment.Refund();
                        AddRefundEntries(payment);
                    }
                }
            }
        }
        else if (payment.Status == PaymentStatus.Authorized)
        {
            switch (newStatus)
            {
                case PaymentStatus.Captured:
                    payment.Capture();
                    AddCaptureEntries(payment);
                    break;
                case PaymentStatus.Voided:
                    payment.Void();
                    break;
                case PaymentStatus.Declined:
                    payment.DeclineAfterAuthorization(result.DeclineReason ?? "provider_declined");
                    break;
                case PaymentStatus.Pending or PaymentStatus.RequiresAction
                    when operation is not null && payment.PendingOperation != operation:
                    payment.MarkOperationPending(operation);
                    break;
                case PaymentStatus.Authorized when payment.PendingOperation is not null &&
                    result.DeclineReason is not null:
                    payment.ClearPendingOperation(result.DeclineReason);
                    break;
            }
        }
        else if (payment.Status == PaymentStatus.Captured)
        {
            if (newStatus == PaymentStatus.Refunded)
            {
                payment.Refund();
                AddRefundEntries(payment);
            }
            else if (newStatus is PaymentStatus.Pending or PaymentStatus.RequiresAction &&
                     operation == "refund" &&
                     payment.PendingOperation != operation)
                payment.MarkOperationPending(operation);
            else if (newStatus == PaymentStatus.Captured && payment.PendingOperation == "refund" &&
                     result.DeclineReason is not null)
                payment.ClearPendingOperation(result.DeclineReason);
        }

        if (payment.Version == oldVersion)
            return null;

        var eventType = priorPendingOperation is not null &&
                        payment.PendingOperation is null &&
                        payment.LastOperationError is not null
            ? $"payment.{priorPendingOperation}_failed"
            : payment.PendingOperation is not null
            ? $"payment.{payment.PendingOperation}_pending"
            : $"payment.{payment.Status.ToString().ToLowerInvariant()}";
        return RecordEvent(payment, eventType);
    }

    private PaymentEvent RecordEvent(Payment payment, string eventType)
    {
        var paymentEvent = new PaymentEvent
        {
            MerchantId = payment.MerchantId,
            PaymentId = payment.Id,
            Type = eventType,
            PayloadJson = JsonSerializer.Serialize(PaymentResponse.FromEntity(payment))
        };
        _db.PaymentEvents.Add(paymentEvent);
        return paymentEvent;
    }

    private void AddCaptureEntries(Payment payment)
    {
        var fee = checked(payment.AmountInCents * _options.FeeBps / 10_000);
        var net = payment.AmountInCents - fee;
        _db.LedgerEntries.AddRange(
            NewEntry(payment, LedgerAccount.AcquirerCash, LedgerEntryType.Debit,
                payment.AmountInCents, "Captura simulada do pagamento"),
            NewEntry(payment, LedgerAccount.MerchantPayable, LedgerEntryType.Credit,
                net, "Crédito demonstrativo ao lojista"),
            NewEntry(payment, LedgerAccount.AcquirerRevenue, LedgerEntryType.Credit,
                fee, "Taxa demonstrativa do gateway"));
    }

    private void AddRefundEntries(Payment payment)
    {
        // Inverte exatamente a taxa registrada na captura, mesmo se a
        // configuração do gateway mudar entre as duas operações.
        var fee = _db.LedgerEntries.Local
            .Where(e => e.PaymentId == payment.Id &&
                        e.Account == LedgerAccount.AcquirerRevenue &&
                        e.Type == LedgerEntryType.Credit)
            .Select(e => (long?)e.AmountInCents)
            .SingleOrDefault()
            ?? _db.LedgerEntries.AsNoTracking()
                .Where(e => e.PaymentId == payment.Id &&
                            e.Account == LedgerAccount.AcquirerRevenue &&
                            e.Type == LedgerEntryType.Credit)
                .Select(e => e.AmountInCents)
                .Single();
        var net = payment.AmountInCents - fee;
        _db.LedgerEntries.AddRange(
            NewEntry(payment, LedgerAccount.AcquirerCash, LedgerEntryType.Credit,
                payment.AmountInCents, "Estorno simulado do pagamento"),
            NewEntry(payment, LedgerAccount.MerchantPayable, LedgerEntryType.Debit,
                net, "Reversão do crédito demonstrativo"),
            NewEntry(payment, LedgerAccount.AcquirerRevenue, LedgerEntryType.Debit,
                fee, "Reversão da taxa demonstrativa"));
    }

    private async Task ValidateAndStoreOperationKeyAsync(
        Guid merchantId, Guid paymentId, string operation, string? key, CancellationToken ct)
    {
        ValidateKey(key);
        if (key is null)
            return;

        var hash = Hash($"{operation}:{paymentId}");
        var existing = await FindKeyAsync(merchantId, key, ct);
        if (existing is not null)
        {
            EnsureKeyMatches(existing, operation, hash);
            return;
        }

        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            MerchantId = merchantId,
            IdempotencyKey = key,
            Operation = operation,
            RequestHash = hash,
            PaymentId = paymentId
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            existing = await FindKeyAsync(merchantId, key, ct);
            if (existing is null)
                throw;
            EnsureKeyMatches(existing, operation, hash);
        }
    }

    private static void ValidateKey(string? key)
    {
        if (key is { Length: > 100 } || (key is not null && string.IsNullOrWhiteSpace(key)))
            throw new DomainException("Idempotency-Key inválida (máximo de 100 caracteres).");
    }

    private static void EnsureKeyMatches(IdempotencyRecord record, string operation, string hash)
    {
        if (record.Operation != operation || record.RequestHash != hash)
            throw new DomainException("Idempotency-Key já utilizada com outros dados ou operação.");
    }

    private Task<IdempotencyRecord?> FindKeyAsync(Guid merchantId, string? key, CancellationToken ct) =>
        key is null
            ? Task.FromResult<IdempotencyRecord?>(null)
            : _db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(
                r => r.MerchantId == merchantId && r.IdempotencyKey == key, ct);

    private async Task<Payment> LoadPaymentAsync(Guid merchantId, Guid paymentId, CancellationToken ct) =>
        await _db.Payments.FirstOrDefaultAsync(
            p => p.Id == paymentId && p.MerchantId == merchantId, ct)
        ?? throw new NotFoundException($"Pagamento {paymentId} não encontrado.");

    private string OperationKey(Guid paymentId, string operation) =>
        $"{_processor.Name}:{paymentId:N}:{operation}";

    private void EnsureConfiguredProvider(Payment payment)
    {
        if (!string.Equals(payment.Provider, _processor.Name, StringComparison.Ordinal))
            throw new DomainException(
                $"Este pagamento usa o provedor {payment.Provider}; o gateway está configurado para {_processor.Name}. Use bancos separados por provedor.");
    }

    private void ValidateProviderResult(Payment payment, ProviderPaymentResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ProviderPaymentId))
            throw new DomainException("O provedor não retornou um identificador de pagamento.");
        if (payment.ProviderPaymentId is not null)
        {
            if (payment.ProviderPaymentId != result.ProviderPaymentId)
                throw new DomainException("Identificador de pagamento do provedor divergente.");
        }

        if (_processor.Name == "stripe" &&
            (payment.ProviderPaymentId is null &&
                (result.GatewayPaymentId is null ||
                 result.AmountInCents is null || result.Currency is null) ||
             result.GatewayPaymentId is not null && result.GatewayPaymentId != payment.Id ||
             result.AmountInCents is not null && result.AmountInCents != payment.AmountInCents ||
             result.Currency is not null &&
                !string.Equals(result.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException(
                "PaymentIntent Stripe não corresponde ao ID, valor e moeda do pagamento local.");
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static LedgerEntry NewEntry(
        Payment payment, LedgerAccount account, LedgerEntryType type, long amountInCents,
        string description) => new()
        {
            MerchantId = payment.MerchantId,
            PaymentId = payment.Id,
            Account = account,
            Type = type,
            AmountInCents = amountInCents,
            Description = description
        };
}
