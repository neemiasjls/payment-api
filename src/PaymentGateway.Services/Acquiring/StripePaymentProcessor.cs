using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PaymentGateway.Domain.Exceptions;
using Stripe;

namespace PaymentGateway.Services.Acquiring;

/// <summary>Stripe PaymentIntents with manual card capture, restricted to Test Mode.</summary>
public sealed class StripePaymentProcessor : IPaymentProcessor
{
    private readonly PaymentIntentService _paymentIntents;
    private readonly RefundService _refunds;

    public StripePaymentProcessor(IOptions<StripeProcessorOptions> options)
        : this(new StripeClient(ValidateSecretKey(options.Value.SecretKey)))
    {
    }

    /// <summary>Allows an official SDK client with a test key to be supplied by tests.</summary>
    public StripePaymentProcessor(IStripeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateSecretKey(client.ApiKey);
        _paymentIntents = new PaymentIntentService(client);
        _refunds = new RefundService(client);
    }

    public string Name => "stripe";

    public async Task<ProviderPaymentResult> AuthorizeAsync(
        ProviderAuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        ValidateOperationKey(request.OperationKey);
        if (request.PaymentId == Guid.Empty || request.AmountInCents <= 0 ||
            string.IsNullOrWhiteSpace(request.Currency))
            throw new DomainException("Parâmetros de autorização inválidos.");

        // The portfolio API has no card-entry UI. Accept only documented shared test methods.
        var fallbackCard = TestPaymentMethods.GetCard(request.PaymentMethodId);
        var options = new PaymentIntentCreateOptions
        {
            Amount = request.AmountInCents,
            Currency = request.Currency.ToLowerInvariant(),
            Description = request.Description,
            PaymentMethod = request.PaymentMethodId,
            PaymentMethodTypes = ["card"],
            CaptureMethod = "manual",
            ConfirmationMethod = "automatic",
            Confirm = true,
            ErrorOnRequiresAction = false,
            Metadata = new Dictionary<string, string>
            {
                ["gateway_payment_id"] = request.PaymentId.ToString("D")
            }
        };
        options.AddExpand("payment_method");
        options.AddExpand("latest_charge");

        var stripeKey = IdempotencyKey("authorize", request.OperationKey, request.PaymentId.ToString("N"));
        try
        {
            var intent = await _paymentIntents.CreateAsync(
                options, new RequestOptions { IdempotencyKey = stripeKey }, cancellationToken);
            return MapIntent(intent, fallbackCard);
        }
        catch (StripeException ex) when (ex.StripeError?.PaymentIntent is not null && IsCardDecline(ex))
        {
            var intent = ex.StripeError!.PaymentIntent!;
            EnsureTestIntent(intent);
            return MapIntent(intent, fallbackCard) with
            {
                Status = ProviderPaymentStatus.Declined,
                DeclineReason = ex.StripeError.DeclineCode ?? ex.StripeError.Code ?? "card_declined"
            };
        }
    }

    public async Task<ProviderPaymentResult> CaptureAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default)
    {
        ValidatePaymentId(providerPaymentId);
        var key = IdempotencyKey("capture", operationKey, providerPaymentId);
        var intent = await _paymentIntents.CaptureAsync(
            providerPaymentId, new PaymentIntentCaptureOptions(),
            new RequestOptions { IdempotencyKey = key }, cancellationToken);
        return MapIntent(intent);
    }

    public async Task<ProviderPaymentResult> VoidAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default)
    {
        ValidatePaymentId(providerPaymentId);
        var key = IdempotencyKey("void", operationKey, providerPaymentId);
        var intent = await _paymentIntents.CancelAsync(
            providerPaymentId, new PaymentIntentCancelOptions(),
            new RequestOptions { IdempotencyKey = key }, cancellationToken);
        return MapIntent(intent);
    }

    public async Task<ProviderPaymentResult> RefundAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default)
    {
        ValidatePaymentId(providerPaymentId);
        var key = IdempotencyKey("refund", operationKey, providerPaymentId);
        var refund = await _refunds.CreateAsync(
            new RefundCreateOptions { PaymentIntent = providerPaymentId },
            new RequestOptions { IdempotencyKey = key }, cancellationToken);

        var status = refund.Status switch
        {
            "succeeded" => ProviderPaymentStatus.Refunded,
            "requires_action" => ProviderPaymentStatus.RequiresAction,
            "failed" or "canceled" => ProviderPaymentStatus.Captured,
            _ => ProviderPaymentStatus.Pending
        };
        return new ProviderPaymentResult(
            providerPaymentId, status, null, null, refund.FailureReason, null);
    }

    public async Task<ProviderPaymentResult> GetAsync(
        string providerPaymentId, CancellationToken cancellationToken = default)
    {
        ValidatePaymentId(providerPaymentId);
        var options = new PaymentIntentGetOptions();
        options.AddExpand("payment_method");
        options.AddExpand("latest_charge");
        var intent = await _paymentIntents.GetAsync(providerPaymentId, options, null, cancellationToken);
        var result = MapIntent(intent);
        if (result.Status != ProviderPaymentStatus.Captured)
            return result;

        // Stripe leaves the PaymentIntent at succeeded after a refund. Inspect refunds
        // before reporting a captured payment during reconciliation.
        var refunds = await _refunds.ListAsync(
            new RefundListOptions { PaymentIntent = providerPaymentId, Limit = 100 },
            null, cancellationToken);
        var fullRefund = refunds.Data.FirstOrDefault(r => r.Amount >= intent.AmountReceived);
        return fullRefund?.Status switch
        {
            "succeeded" => result with { Status = ProviderPaymentStatus.Refunded },
            "requires_action" => result with { Status = ProviderPaymentStatus.RequiresAction },
            "pending" => result with { Status = ProviderPaymentStatus.Pending },
            _ => result
        };
    }

    private static ProviderPaymentResult MapIntent(
        PaymentIntent intent, (string Last4, string Brand)? fallbackCard = null)
    {
        EnsureTestIntent(intent);
        var card = intent.PaymentMethod?.Card;
        var chargedCard = intent.LatestCharge?.PaymentMethodDetails?.Card;
        var status = intent.Status switch
        {
            "requires_capture" => ProviderPaymentStatus.Authorized,
            "succeeded" => ProviderPaymentStatus.Captured,
            "canceled" => ProviderPaymentStatus.Voided,
            "requires_payment_method" => ProviderPaymentStatus.Declined,
            "requires_action" => ProviderPaymentStatus.RequiresAction,
            _ => ProviderPaymentStatus.Pending
        };
        return new ProviderPaymentResult(
            intent.Id,
            status,
            card?.Last4 ?? chargedCard?.Last4 ?? fallbackCard?.Last4,
            card?.Brand ?? chargedCard?.Brand ?? fallbackCard?.Brand,
            intent.LastPaymentError?.DeclineCode ?? intent.LastPaymentError?.Code,
            status == ProviderPaymentStatus.RequiresAction ? intent.ClientSecret : null);
    }

    private static bool IsCardDecline(StripeException exception) =>
        exception.StripeError?.Type == "card_error" ||
        exception.StripeError?.Code is "card_declined" or "expired_card" or "incorrect_cvc";

    private static void EnsureTestIntent(PaymentIntent intent)
    {
        if (intent.Livemode || string.IsNullOrWhiteSpace(intent.Id))
            throw new InvalidOperationException("A Stripe respondeu fora do modo de teste.");
    }

    private static string ValidateSecretKey(string? secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretKey) ||
            !secretKey.StartsWith("sk_test_", StringComparison.Ordinal) ||
            secretKey.Length <= "sk_test_".Length)
            throw new InvalidOperationException("Configure uma chave secreta Stripe sk_test_ para o modo sandbox.");
        return secretKey;
    }

    private static void ValidatePaymentId(string providerPaymentId)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentId) ||
            !providerPaymentId.StartsWith("pi_", StringComparison.Ordinal))
            throw new DomainException("ID de PaymentIntent inválido.");
    }

    private static void ValidateOperationKey(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new DomainException("A chave de idempotência é obrigatória.");
    }

    private static string IdempotencyKey(string operation, string operationKey, string providerIdentity)
    {
        ValidateOperationKey(operationKey);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{providerIdentity}\n{operationKey}"));
        return $"payment-gateway:{operation}:{Convert.ToHexStringLower(digest)}";
    }
}
