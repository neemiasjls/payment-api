namespace PaymentGateway.Services.Acquiring;

public sealed class StripeProcessorOptions
{
    public const string SectionName = "Stripe";

    /// <summary>Only an sk_test_ secret key is accepted.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Stripe CLI or Dashboard signing secret for the inbound webhook.</summary>
    public string WebhookSecret { get; set; } = string.Empty;
}
