using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Controllers;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Services;
using PaymentGateway.Services.Acquiring;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Tests;

public sealed class StripeWebhooksControllerTests
{
    private const string Secret = "whsec_test_webhook_signature";

    [Fact]
    public async Task ValidSignedTestEvent_IsForwardedForReconciliation()
    {
        var payments = new RecordingPayments();
        var controller = NewController(payments, ValidBody());

        var result = await controller.Receive(CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal("evt_test_123", payments.EventId);
        Assert.Equal("pi_test_123", payments.ProviderPaymentId);
        Assert.Equal(Guid.Parse("fa8a87cf-11b0-4b20-a83c-0093878111c9"), payments.LocalPaymentId);
    }

    [Fact]
    public async Task InvalidSignature_IsRejectedBeforeReconciliation()
    {
        var payments = new RecordingPayments();
        var controller = NewController(payments, ValidBody(), signature: "t=1,v1=bad");

        var result = await controller.Receive(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Null(payments.EventId);
    }

    [Fact]
    public async Task SignedMalformedJson_IsRejectedAsBadRequest()
    {
        var payments = new RecordingPayments();
        var controller = NewController(payments, "{");

        var result = await controller.Receive(CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.Null(payments.EventId);
    }

    private static string ValidBody() =>
        """
        {"id":"evt_test_123","type":"payment_intent.succeeded","livemode":false,"data":{"object":{"id":"pi_test_123","metadata":{"gateway_payment_id":"fa8a87cf-11b0-4b20-a83c-0093878111c9"}}}}
        """;

    private static StripeWebhooksController NewController(
        RecordingPayments payments, string body, string? signature = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), payload);
        signature ??= $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";

        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["Stripe-Signature"] = signature;
        return new StripeWebhooksController(
            payments, new StripeNamedProcessor(),
            Options.Create(new StripeProcessorOptions { WebhookSecret = Secret }))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class StripeNamedProcessor : IPaymentProcessor
    {
        public string Name => "stripe";
        public Task<ProviderPaymentResult> AuthorizeAsync(
            ProviderAuthorizeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderPaymentResult> CaptureAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderPaymentResult> VoidAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderPaymentResult> RefundAsync(
            string id, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderPaymentResult> GetAsync(
            string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingPayments : IPaymentService
    {
        public string? EventId { get; private set; }
        public string? ProviderPaymentId { get; private set; }
        public Guid? LocalPaymentId { get; private set; }

        public Task ReconcileProviderEventAsync(
            string eventId, string providerPaymentId, Guid? localPaymentId, CancellationToken ct = default)
        {
            EventId = eventId;
            ProviderPaymentId = providerPaymentId;
            LocalPaymentId = localPaymentId;
            return Task.CompletedTask;
        }

        public Task<(PaymentResponse Payment, bool Created)> AuthorizeAsync(
            Guid merchantId, CreatePaymentRequest request, string? key, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PaymentResponse> CaptureAsync(
            Guid merchantId, Guid paymentId, string? key = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PaymentResponse> VoidAsync(
            Guid merchantId, Guid paymentId, string? key = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PaymentResponse> RefundAsync(
            Guid merchantId, Guid paymentId, string? key = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PaymentResponse> GetAsync(
            Guid merchantId, Guid paymentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PaymentResponse> RefreshAsync(
            Guid merchantId, Guid paymentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PagedResponse<PaymentResponse>> ListAsync(
            Guid merchantId, int page, int pageSize, PaymentStatus? status, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PaymentEventResponse>> GetEventsAsync(
            Guid merchantId, Guid paymentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
