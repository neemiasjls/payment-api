namespace PaymentGateway.Domain.Entities;

/// <summary>
/// Evento do ciclo de vida de um pagamento (payment.authorized, payment.captured...).
/// Serve como trilha de auditoria e como fila de entrega de webhooks.
/// </summary>
public class PaymentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MerchantId { get; set; }
    public Guid PaymentId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Controle de entrega do webhook.
    public int DeliveryAttempts { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public string? LastDeliveryError { get; set; }

    // O registro é a fonte da verdade da fila de saída. Um worker pode
    // retomar eventos vencidos após reinício sem depender de memória local.
    public DateTime? NextDeliveryAttemptAtUtc { get; set; }
    public DateTime? DeliveryLeaseUntilUtc { get; set; }
    public Guid? DeliveryLeaseToken { get; set; }
    public DateTime? DeliverySkippedAtUtc { get; set; }
}
