using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentGateway.Data;

namespace PaymentGateway.Services.Webhooks;

/// <summary>
/// Serviço em background que entrega webhooks aos lojistas com retentativas.
/// Roda fora do ciclo requisição/resposta: a API nunca fica esperando o
/// servidor do lojista responder.
/// </summary>
public class WebhookDispatcher : BackgroundService
{
    public const string HttpClientName = "webhooks";
    private const int MaxAttempts = 3;

    private readonly IWebhookQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(
        IWebhookQueue queue,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatcher> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid eventId;
            try
            {
                eventId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DeliverAsync(eventId, stoppingToken);
            }
            catch (Exception ex)
            {
                // Uma falha de entrega não pode derrubar o serviço inteiro.
                _logger.LogError(ex, "Erro ao entregar webhook do evento {EventId}", eventId);
            }
        }
    }

    private async Task DeliverAsync(Guid eventId, CancellationToken ct)
    {
        // BackgroundService é singleton e DbContext é scoped: cada entrega
        // abre seu próprio escopo para obter um DbContext isolado.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var paymentEvent = await db.PaymentEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (paymentEvent is null)
            return;

        var merchant = await db.Merchants.FirstOrDefaultAsync(m => m.Id == paymentEvent.MerchantId, ct);
        if (merchant?.WebhookUrl is null)
            return; // Lojista não configurou webhook: o evento fica só como auditoria.

        var client = _httpClientFactory.CreateClient(HttpClientName);

        var body = $$"""
            {"id":"{{paymentEvent.Id}}","type":"{{paymentEvent.Type}}","createdAtUtc":"{{paymentEvent.CreatedAtUtc:O}}","data":{{paymentEvent.PayloadJson}}}
            """;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            paymentEvent.DeliveryAttempts = attempt;

            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(merchant.WebhookUrl, content, ct);

                if (response.IsSuccessStatusCode)
                {
                    paymentEvent.DeliveredAtUtc = DateTime.UtcNow;
                    paymentEvent.LastDeliveryError = null;
                    break;
                }

                paymentEvent.LastDeliveryError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                paymentEvent.LastDeliveryError = ex.Message;
            }

            if (attempt < MaxAttempts)
            {
                // Backoff exponencial simples: 2s, 4s...
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        await db.SaveChangesAsync(ct);

        if (paymentEvent.DeliveredAtUtc is null)
        {
            _logger.LogWarning(
                "Webhook {EventType} do pagamento {PaymentId} falhou após {Attempts} tentativas: {Error}",
                paymentEvent.Type, paymentEvent.PaymentId, paymentEvent.DeliveryAttempts, paymentEvent.LastDeliveryError);
        }
    }
}
