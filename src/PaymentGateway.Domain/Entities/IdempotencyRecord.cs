namespace PaymentGateway.Domain.Entities;

/// <summary>
/// Garante idempotência na criação de pagamentos: se o cliente reenviar a
/// mesma requisição (mesma Idempotency-Key), devolvemos o pagamento original
/// em vez de cobrar duas vezes. Essencial em redes instáveis.
/// </summary>
public class IdempotencyRecord
{
    public long Id { get; set; }
    public Guid MerchantId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public Guid PaymentId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
