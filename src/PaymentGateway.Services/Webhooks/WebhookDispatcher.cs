using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentGateway.Data;
using PaymentGateway.Domain.Entities;

namespace PaymentGateway.Services.Webhooks;

/// <summary>
/// Entrega eventos persistidos no banco. O sinal em memória reduz a latência,
/// mas a varredura periódica retoma pendências após restart ou sinal perdido.
/// </summary>
public sealed class WebhookDispatcher : BackgroundService
{
    public const string HttpClientName = "webhooks";
    private const int MaxAttempts = 5;
    private const int BatchSize = 32;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    private readonly IWebhookQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly HttpClient _client;

    public WebhookDispatcher(
        IWebhookQueue queue,
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<WebhookDispatcher> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
        _client = new HttpClient(CreateSafeHandler(AllowLoopback), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private bool AllowLoopback =>
        _environment.IsDevelopment() || _environment.IsEnvironment("Testing");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Uma falha no banco ou em um evento não pode encerrar o worker.
                _logger.LogError(ex, "Falha no dispatcher de webhooks");
            }

            using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            poll.CancelAfter(PollInterval);
            try
            {
                await _queue.DequeueAsync(poll.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Venceu a espera: buscar novamente no banco.
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Executa uma varredura; exposto para testes e operações manuais.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var candidateIds = await db.PaymentEvents.AsNoTracking()
            .Where(e => e.DeliveredAtUtc == null &&
                        e.DeliverySkippedAtUtc == null &&
                        e.DeliveryAttempts < MaxAttempts &&
                        (e.NextDeliveryAttemptAtUtc == null || e.NextDeliveryAttemptAtUtc <= now) &&
                        (e.DeliveryLeaseUntilUtc == null || e.DeliveryLeaseUntilUtc <= now))
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var claimedCount = 0;
        foreach (var eventId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryClaimAndDeliverAsync(db, eventId, ct))
                    claimedCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // O lease expira se a finalização falhar, permitindo replay.
                _logger.LogError(ex, "Falha no webhook {EventId}; o lease permitirá nova tentativa", eventId);
            }
        }

        return claimedCount;
    }

    private async Task<bool> TryClaimAndDeliverAsync(AppDbContext db, Guid eventId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseToken = Guid.NewGuid();
        var leaseUntil = now.Add(LeaseDuration);

        // UPDATE condicional é o lock entre instâncias. Apenas o dono do
        // token pode finalizar a tentativa; leases órfãos expiram.
        var claimed = await db.PaymentEvents
            .Where(e => e.Id == eventId && e.DeliveredAtUtc == null &&
                        e.DeliverySkippedAtUtc == null &&
                        e.DeliveryAttempts < MaxAttempts &&
                        (e.NextDeliveryAttemptAtUtc == null || e.NextDeliveryAttemptAtUtc <= now) &&
                        (e.DeliveryLeaseUntilUtc == null || e.DeliveryLeaseUntilUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.DeliveryLeaseToken, (Guid?)leaseToken)
                .SetProperty(e => e.DeliveryLeaseUntilUtc, (DateTime?)leaseUntil), ct);

        if (claimed == 0)
            return false;

        var paymentEvent = await db.PaymentEvents.AsNoTracking()
            .SingleAsync(e => e.Id == eventId, ct);
        var merchant = await db.Merchants.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == paymentEvent.MerchantId, ct);

        if (merchant?.WebhookUrl is null || merchant.WebhookSecret is null)
        {
            await MarkSkippedAsync(db, eventId, leaseToken, ct);
            return true;
        }

        if (!WebhookTargetValidator.TryValidate(
                merchant.WebhookUrl, AllowLoopback, out var target))
        {
            await CompleteAttemptAsync(db, paymentEvent, leaseToken, "UnsafeDestination", false, ct);
            return true;
        }

        string body;
        try
        {
            using var payload = JsonDocument.Parse(paymentEvent.PayloadJson);
            body = JsonSerializer.Serialize(new
            {
                id = paymentEvent.Id,
                type = paymentEvent.Type,
                createdAtUtc = paymentEvent.CreatedAtUtc,
                data = payload.RootElement
            });
        }
        catch (JsonException)
        {
            await CompleteAttemptAsync(db, paymentEvent, leaseToken, "InvalidPayload", false, ct);
            return true;
        }

        string? error = null;
        var delivered = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Webhook-Signature", WebhookSigner.Sign(body, merchant.WebhookSecret));
            request.Headers.Add("X-Webhook-Event-Id", paymentEvent.Id.ToString());

            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            delivered = response.IsSuccessStatusCode;
            if (!delivered)
                error = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "Timeout";
        }
        catch (HttpRequestException)
        {
            // Exceções HTTP podem conter query strings e hosts privados.
            error = "NetworkError";
        }

        await CompleteAttemptAsync(db, paymentEvent, leaseToken, error, delivered, ct);
        return true;
    }

    private static Task<int> MarkSkippedAsync(
        AppDbContext db, Guid eventId, Guid leaseToken, CancellationToken ct) =>
        db.PaymentEvents
            .Where(e => e.Id == eventId && e.DeliveryLeaseToken == leaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.DeliverySkippedAtUtc, (DateTime?)DateTime.UtcNow)
                .SetProperty(e => e.DeliveryLeaseToken, (Guid?)null)
                .SetProperty(e => e.DeliveryLeaseUntilUtc, (DateTime?)null), ct);

    private async Task CompleteAttemptAsync(
        AppDbContext db, PaymentEvent paymentEvent, Guid leaseToken,
        string? error, bool delivered, CancellationToken ct)
    {
        var attempt = paymentEvent.DeliveryAttempts + 1;
        var now = DateTime.UtcNow;
        var nextAttempt = delivered || attempt >= MaxAttempts
            ? (DateTime?)null
            : now.AddSeconds(Math.Pow(2, attempt));

        var updated = await db.PaymentEvents
            .Where(e => e.Id == paymentEvent.Id && e.DeliveryLeaseToken == leaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.DeliveryAttempts, attempt)
                .SetProperty(e => e.DeliveredAtUtc, delivered ? (DateTime?)now : null)
                .SetProperty(e => e.LastDeliveryError, error)
                .SetProperty(e => e.NextDeliveryAttemptAtUtc, nextAttempt)
                .SetProperty(e => e.DeliveryLeaseToken, (Guid?)null)
                .SetProperty(e => e.DeliveryLeaseUntilUtc, (DateTime?)null), ct);

        if (updated == 1 && !delivered && attempt >= MaxAttempts)
            _logger.LogWarning(
                "Webhook {EventId} esgotou {Attempts} tentativas: {Error}",
                paymentEvent.Id, attempt, error);
    }

    private static SocketsHttpHandler CreateSafeHandler(bool allowLoopback) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = (context, ct) =>
            WebhookTargetValidator.ConnectAsync(context, allowLoopback, ct)
    };

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
