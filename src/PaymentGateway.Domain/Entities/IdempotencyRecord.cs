namespace PaymentGateway.Domain.Entities;

/// <summary>
/// Garante idempotência nas operações que alteram pagamentos (authorize,
/// capture, void e refund): se o cliente reenviar a mesma requisição (mesma
/// Idempotency-Key), devolvemos o resultado original em vez de repetir a
/// operação. Essencial em redes instáveis.
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
