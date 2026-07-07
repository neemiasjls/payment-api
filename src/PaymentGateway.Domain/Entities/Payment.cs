using PaymentGateway.Domain.Enums;
using PaymentGateway.Domain.Exceptions;

namespace PaymentGateway.Domain.Entities;

/// <summary>
/// Uma transação de pagamento. As transições de estado são controladas
/// pela própria entidade para que nenhum fluxo inválido (ex.: estornar
/// algo que não foi capturado) seja possível, independente de quem chama.
/// </summary>
public class Payment
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid MerchantId { get; private set; }

    /// <summary>
    /// Valor em centavos (ex.: R$ 10,00 = 1000). Inteiros evitam erros de
    /// arredondamento de ponto flutuante — nunca use float/double para dinheiro.
    /// </summary>
    public long AmountInCents { get; private set; }

    public string Currency { get; private set; } = "BRL";

    // Dados sensíveis do cartão (número completo, CVV) NUNCA são persistidos,
    // seguindo o princípio do PCI DSS. Guardamos só o necessário para exibição.
    public string CardLast4 { get; private set; } = string.Empty;
    public string CardBrand { get; private set; } = "Unknown";

    public string? Description { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? DeclineReason { get; private set; }

    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CapturedAtUtc { get; private set; }
    public DateTime? VoidedAtUtc { get; private set; }
    public DateTime? RefundedAtUtc { get; private set; }

    private Payment()
    {
        // Construtor exigido pelo EF Core.
    }

    public static Payment Authorize(
        Guid merchantId, long amountInCents, string currency,
        string cardLast4, string cardBrand, string? description)
    {
        if (amountInCents <= 0)
            throw new DomainException("O valor do pagamento deve ser maior que zero.");

        return new Payment
        {
            MerchantId = merchantId,
            AmountInCents = amountInCents,
            Currency = currency,
            CardLast4 = cardLast4,
            CardBrand = cardBrand,
            Description = description,
            Status = PaymentStatus.Authorized
        };
    }

    public static Payment Decline(
        Guid merchantId, long amountInCents, string currency,
        string cardLast4, string cardBrand, string? description, string reason)
    {
        return new Payment
        {
            MerchantId = merchantId,
            AmountInCents = amountInCents,
            Currency = currency,
            CardLast4 = cardLast4,
            CardBrand = cardBrand,
            Description = description,
            Status = PaymentStatus.Declined,
            DeclineReason = reason
        };
    }

    /// <summary>Confirma a cobrança de um pagamento autorizado (o dinheiro "entra").</summary>
    public void Capture()
    {
        if (Status != PaymentStatus.Authorized)
            throw new DomainException(
                $"Só é possível capturar pagamentos autorizados. Status atual: {Status}.");

        Status = PaymentStatus.Captured;
        CapturedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Cancela uma autorização que ainda não foi capturada.</summary>
    public void Void()
    {
        if (Status != PaymentStatus.Authorized)
            throw new DomainException(
                $"Só é possível cancelar pagamentos autorizados e não capturados. Status atual: {Status}.");

        Status = PaymentStatus.Voided;
        VoidedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Devolve o valor de um pagamento já capturado.</summary>
    public void Refund()
    {
        if (Status != PaymentStatus.Captured)
            throw new DomainException(
                $"Só é possível estornar pagamentos capturados. Status atual: {Status}.");

        Status = PaymentStatus.Refunded;
        RefundedAtUtc = DateTime.UtcNow;
    }
}
