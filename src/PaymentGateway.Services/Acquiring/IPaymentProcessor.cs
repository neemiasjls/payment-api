namespace PaymentGateway.Services.Acquiring;

/// <summary>The provider's observed payment state. Only Authorized can be captured.</summary>
public enum ProviderPaymentStatus
{
    Authorized,
    Captured,
    Voided,
    Refunded,
    Declined,
    Pending,
    RequiresAction
}

/// <summary>Uses a payment method token, never card details.</summary>
public sealed record ProviderAuthorizeRequest(
    Guid PaymentId,
    long AmountInCents,
    string Currency,
    string PaymentMethodId,
    string? Description,
    string OperationKey);

public sealed record ProviderPaymentResult(
    string ProviderPaymentId,
    ProviderPaymentStatus Status,
    string? CardLast4,
    string? CardBrand,
    string? DeclineReason,
    string? ClientSecret);

public interface IPaymentProcessor
{
    string Name { get; }

    Task<ProviderPaymentResult> AuthorizeAsync(
        ProviderAuthorizeRequest request, CancellationToken cancellationToken = default);

    Task<ProviderPaymentResult> CaptureAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default);

    Task<ProviderPaymentResult> VoidAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default);

    Task<ProviderPaymentResult> RefundAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default);

    Task<ProviderPaymentResult> GetAsync(
        string providerPaymentId, CancellationToken cancellationToken = default);
}
