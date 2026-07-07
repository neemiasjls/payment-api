namespace PaymentGateway.Domain.Entities;

/// <summary>
/// Lojista (estabelecimento comercial) que processa pagamentos pelo gateway.
/// A API key nunca é armazenada em texto puro — apenas o hash SHA-256 dela.
/// </summary>
public class Merchant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;

    /// <summary>URL opcional para onde o gateway envia notificações de eventos (webhooks).</summary>
    public string? WebhookUrl { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
