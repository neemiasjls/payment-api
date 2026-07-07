using System.Threading.Channels;

namespace PaymentGateway.Services.Webhooks;

/// <summary>
/// Fila em memória que desacopla o processamento do pagamento da entrega
/// do webhook: a API responde rápido e a notificação sai em background.
/// </summary>
public interface IWebhookQueue
{
    void Enqueue(Guid paymentEventId);
    ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
}

public class WebhookQueue : IWebhookQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid paymentEventId) =>
        _channel.Writer.TryWrite(paymentEventId);

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
