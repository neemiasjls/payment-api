using PaymentGateway.Domain.Enums;

namespace PaymentGateway.Domain.Entities;

/// <summary>
/// Lançamento contábil imutável (razão de partidas dobradas).
/// O saldo de um lojista nunca é um campo atualizado no banco:
/// ele é sempre derivado da soma dos lançamentos, o que dá
/// rastreabilidade completa de cada centavo.
/// </summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid PaymentId { get; set; }
    public LedgerAccount Account { get; set; }
    public LedgerEntryType Type { get; set; }
    public long AmountInCents { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
