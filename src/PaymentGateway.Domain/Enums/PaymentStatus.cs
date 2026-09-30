namespace PaymentGateway.Domain.Enums;

/// <summary>
/// Ciclo de vida de uma transação:
/// Pending -> RequiresAction -> Authorized (ou Declined, pelo resultado do provedor)
/// Authorized -> Captured -> Refunded
/// Authorized -> Voided
/// (Voided, Refunded e Declined são estados finais; Authorized também pode virar Declined por recusa tardia)
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
