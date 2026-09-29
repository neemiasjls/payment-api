namespace PaymentGateway.Services.Acquiring;

/// <summary>
/// Deterministic outcomes for Stripe's documented test PaymentMethod identifiers.
/// This local simulator never accepts a card number or security code.
/// </summary>
public static class AcquirerSimulator
{
    public static string? Authorize(string paymentMethodId) => paymentMethodId switch
    {
        "pm_card_visa_chargeDeclined" => "generic_decline",
        "pm_card_visa_chargeDeclinedInsufficientFunds" => "insufficient_funds",
        "pm_card_chargeDeclinedExpiredCard" => "expired_card",
        _ => null
    };
}
