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
    private readonly object _gate = new();
    private Guid _lastEventId;

    public void Enqueue(Guid paymentEventId)
    {
        // O ID já está persistido. Um único sinal pendente basta, pois o
        // dispatcher sempre consulta todos os eventos vencidos no banco.
        lock (_gate)
        {
            _lastEventId = paymentEventId;
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
    }

    public async ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken);
        lock (_gate)
            return _lastEventId;
    }

    public void Dispose() => _signal.Dispose();
}
