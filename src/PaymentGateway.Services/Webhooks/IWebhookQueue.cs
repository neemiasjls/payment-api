namespace PaymentGateway.Services.Webhooks;

/// <summary>
/// Sinal para acordar o dispatcher após um commit. Os eventos pendentes ficam
/// no banco; perder este sinal não perde uma entrega.
/// </summary>
public interface IWebhookQueue
{
    void Enqueue(Guid paymentEventId);
    ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class WebhookQueue : IWebhookQueue, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Enqueue(Guid paymentEventId)
    {
        // O ID já está persistido. Um único sinal pendente basta, pois o
        // dispatcher sempre consulta todos os eventos vencidos no banco.
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { /* outro produtor sinalizou */ }
        }
    }

    public async ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken);
        return Guid.Empty;
    }

    public void Dispose() => _signal.Dispose();
}
