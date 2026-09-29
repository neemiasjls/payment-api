namespace PaymentGateway.Domain.Enums;

/// <summary>
/// Ciclo de vida de uma transação:
/// Authorized -> Captured -> Refunded
/// Authorized -> Voided
/// (Declined é um estado final, criado quando o emissor recusa o cartão)
/// </summary>
public enum PaymentStatus
{
    Pending,
    RequiresAction,
    Authorized,
    Captured,
    Voided,
    Refunded,
    Declined
}
