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

    // Apenas metadados devolvidos pelo provedor; a API não recebe PAN ou CVV.
    public string CardLast4 { get; private set; } = string.Empty;
    public string CardBrand { get; private set; } = "Unknown";
    public string Provider { get; private set; } = string.Empty;
    public string? ProviderPaymentId { get; private set; }
    public string? PendingOperation { get; private set; }
    public string? LastOperationError { get; private set; }

    // Token de concorrência que funciona também no SQLite.
    public int Version { get; private set; }

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

    public static Payment CreatePending(
        Guid merchantId, long amountInCents, string currency, string? description, string provider)
    {
        if (amountInCents is < 50 or > 99_999_999)
            throw new DomainException("O valor deve estar entre 50 e 99.999.999 centavos.");
        if (currency != "BRL")
            throw new DomainException("A demonstração aceita apenas BRL.");

        return new Payment
        {
            MerchantId = merchantId,
            AmountInCents = amountInCents,
            Currency = currency,
            Description = description,
            Provider = provider,
            Status = PaymentStatus.Pending
        };
    }

    public void ApplyAuthorization(
        string providerPaymentId, PaymentStatus status, string? cardLast4,
        string? cardBrand, string? declineReason)
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.RequiresAction))
            throw new DomainException($"Não é possível atualizar autorização em {Status}.");
        if (status is not (PaymentStatus.Pending or PaymentStatus.RequiresAction or
            PaymentStatus.Authorized or PaymentStatus.Declined))
            throw new DomainException("Status de autorização inválido.");

        ProviderPaymentId = providerPaymentId;
        CardLast4 = cardLast4 ?? string.Empty;
        CardBrand = cardBrand ?? "Unknown";
        DeclineReason = declineReason;
        Status = status;
        Version++;
    }

    public void MarkOperationPending(string operation)
    {
        if (PendingOperation is not null && PendingOperation != operation)
            throw new DomainException($"A operação {PendingOperation} ainda está pendente.");
        PendingOperation = operation;
        LastOperationError = null;
        Version++;
    }

    public void ClearPendingOperation(string? error)
    {
        if (PendingOperation is null)
            return;
        PendingOperation = null;
        LastOperationError = error;
        Version++;
    }

    public void DeclineAfterAuthorization(string reason)
    {
        if (Status != PaymentStatus.Authorized)
            throw new DomainException($"Não é possível recusar pagamento em {Status}.");
        Status = PaymentStatus.Declined;
        DeclineReason = reason;
        PendingOperation = null;
        Version++;
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
        PendingOperation = null;
        LastOperationError = null;
        CapturedAtUtc = DateTime.UtcNow;
        Version++;
    }

    /// <summary>Cancela uma autorização que ainda não foi capturada.</summary>
    public void Void()
    {
        if (Status != PaymentStatus.Authorized)
            throw new DomainException(
                $"Só é possível cancelar pagamentos autorizados e não capturados. Status atual: {Status}.");

        Status = PaymentStatus.Voided;
        PendingOperation = null;
        LastOperationError = null;
        VoidedAtUtc = DateTime.UtcNow;
        Version++;
    }

    /// <summary>Devolve o valor de um pagamento já capturado.</summary>
    public void Refund()
    {
        if (Status != PaymentStatus.Captured)
            throw new DomainException(
                $"Só é possível estornar pagamentos capturados. Status atual: {Status}.");

        Status = PaymentStatus.Refunded;
        PendingOperation = null;
        LastOperationError = null;
        RefundedAtUtc = DateTime.UtcNow;
        Version++;
    }
}
