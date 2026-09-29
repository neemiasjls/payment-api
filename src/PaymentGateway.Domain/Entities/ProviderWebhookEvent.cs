namespace PaymentGateway.Domain.Entities;

/// <summary>Marca eventos de entrada já reconciliados para ignorar entregas duplicadas.</summary>
public class ProviderWebhookEvent
{
    public string Provider { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string? ProviderPaymentId { get; set; }
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}
