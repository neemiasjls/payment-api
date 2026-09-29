using PaymentGateway.Domain.Exceptions;

namespace PaymentGateway.Services.Acquiring;

/// <summary>Small allowlist of PaymentMethods published in Stripe's testing documentation.</summary>
public static class TestPaymentMethods
{
    public static (string Last4, string Brand) GetCard(string paymentMethodId) => paymentMethodId switch
    {
        "pm_card_visa" => ("4242", "visa"),
        "pm_card_visa_debit" => ("5556", "visa"),
        "pm_card_mastercard" => ("4444", "mastercard"),
        "pm_card_mastercard_debit" => ("8210", "mastercard"),
        "pm_card_amex" => ("0005", "amex"),
        "pm_card_visa_chargeDeclined" => ("0002", "visa"),
        "pm_card_visa_chargeDeclinedInsufficientFunds" => ("9995", "visa"),
        "pm_card_chargeDeclinedExpiredCard" => ("0069", "visa"),
        "pm_card_authenticationRequired" => (string.Empty, "Unknown"),
        _ => throw new DomainException(
            "Use apenas um PaymentMethod de teste documentado pela Stripe.")
    };
}
