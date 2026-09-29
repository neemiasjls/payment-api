namespace PaymentGateway.Services.Reconciliation;

/// <summary>Controls the bounded, periodic Stripe reconciliation sweep.</summary>
public sealed class PaymentReconciliationOptions
{
    public const string SectionName = "PaymentReconciliation";

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; set; } = 50;
}
