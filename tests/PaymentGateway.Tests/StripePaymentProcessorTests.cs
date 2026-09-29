using System.Net;
using Microsoft.Extensions.Options;
using PaymentGateway.Domain.Exceptions;
using PaymentGateway.Services.Acquiring;
using Stripe;

namespace PaymentGateway.Tests;

public class StripePaymentProcessorTests
{
    [Fact]
    public void Stripe_RejectsLiveOrMissingKeys()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new StripePaymentProcessor(Options.Create(new StripeProcessorOptions
            {
                SecretKey = "sk_live_not_allowed"
            })));
        Assert.Throws<InvalidOperationException>(() =>
            new StripePaymentProcessor(Options.Create(new StripeProcessorOptions())));
    }

    [Fact]
    public async Task Fake_PreservesManualCaptureLifecycleAndIdempotency()
    {
        var processor = new FakePaymentProcessor();
        var request = new ProviderAuthorizeRequest(
            Guid.NewGuid(), 1000, "BRL", "pm_card_visa", "Pedido", "create-1");

        var authorized = await processor.AuthorizeAsync(request);
        var retry = await processor.AuthorizeAsync(request);
        var captured = await processor.CaptureAsync(authorized.ProviderPaymentId, "capture-1");
        var capturedRetry = await processor.CaptureAsync(authorized.ProviderPaymentId, "capture-1");
        var refunded = await processor.RefundAsync(authorized.ProviderPaymentId, "refund-1");

        Assert.Equal(ProviderPaymentStatus.Authorized, authorized.Status);
        Assert.Equal(authorized, retry);
        Assert.Equal(ProviderPaymentStatus.Captured, captured.Status);
        Assert.Equal(captured, capturedRetry);
        Assert.Equal(ProviderPaymentStatus.Refunded, refunded.Status);
        Assert.Equal(refunded, await processor.GetAsync(authorized.ProviderPaymentId));
        Assert.Equal(authorized, await processor.AuthorizeAsync(request));
        Assert.Equal(captured, await processor.CaptureAsync(authorized.ProviderPaymentId, "capture-1"));
        await Assert.ThrowsAsync<DomainException>(() =>
            processor.VoidAsync(authorized.ProviderPaymentId, "void-after-refund"));
    }

    [Theory]
    [InlineData("pm_card_visa_chargeDeclined", "generic_decline")]
    [InlineData("pm_card_visa_chargeDeclinedInsufficientFunds", "insufficient_funds")]
    [InlineData("pm_card_chargeDeclinedExpiredCard", "expired_card")]
    public async Task Fake_MapsDocumentedDeclines(string method, string reason)
    {
        var processor = new FakePaymentProcessor();
        var result = await processor.AuthorizeAsync(new ProviderAuthorizeRequest(
            Guid.NewGuid(), 1000, "BRL", method, null, "create-declined"));

        Assert.Equal(ProviderPaymentStatus.Declined, result.Status);
        Assert.Equal(reason, result.DeclineReason);
    }

    [Fact]
    public async Task Fake_RejectsReusedPaymentIdWithDifferentPayload()
    {
        var processor = new FakePaymentProcessor();
        var paymentId = Guid.NewGuid();
        await processor.AuthorizeAsync(new ProviderAuthorizeRequest(
            paymentId, 1000, "BRL", "pm_card_visa", null, "same"));

        await Assert.ThrowsAsync<DomainException>(() => processor.AuthorizeAsync(
            new ProviderAuthorizeRequest(paymentId, 2000, "BRL", "pm_card_visa", null, "same")));
    }

    [Fact]
    public async Task Stripe_UsesManualCaptureAndStableOperationKeys()
    {
        var http = new StubStripeHttpClient(request => request.Uri.AbsolutePath switch
        {
            "/v1/payment_intents" => IntentJson("requires_capture"),
            "/v1/payment_intents/pi_test/capture" => IntentJson("succeeded"),
            "/v1/payment_intents/pi_test/cancel" => IntentJson("canceled"),
            "/v1/payment_intents/pi_test" => IntentJson("succeeded"),
            "/v1/refunds" when request.Uri.Query.Length > 0 => """
                {"object":"list","data":[{"id":"re_test","object":"refund","amount":1000,"status":"succeeded","payment_intent":"pi_test"}],"has_more":false}
                """,
            "/v1/refunds" => """
                {"id":"re_test","object":"refund","amount":1000,"status":"succeeded","payment_intent":"pi_test"}
                """,
            _ => throw new InvalidOperationException($"Unexpected Stripe path: {request.Uri.AbsolutePath}")
        });
        var processor = NewStripeProcessor(http);
        var paymentId = Guid.NewGuid();
        var request = new ProviderAuthorizeRequest(
            paymentId, 1000, "BRL", "pm_card_visa", "Pedido", "create-1");

        var authorized = await processor.AuthorizeAsync(request);
        var authorizedRetry = await processor.AuthorizeAsync(request);
        var captured = await processor.CaptureAsync("pi_test", "capture-1");
        var refunded = await processor.RefundAsync("pi_test", "refund-1");

        Assert.Equal(ProviderPaymentStatus.Authorized, authorized.Status);
        Assert.Equal(authorized, authorizedRetry);
        Assert.Equal(ProviderPaymentStatus.Captured, captured.Status);
        Assert.Equal(ProviderPaymentStatus.Refunded, refunded.Status);

        var createCalls = http.Calls.Where(c => c.Path == "/v1/payment_intents").ToArray();
        Assert.Equal(2, createCalls.Length);
        Assert.Equal(createCalls[0].IdempotencyKey, createCalls[1].IdempotencyKey);
        Assert.Contains("capture_method=manual", createCalls[0].Body);
        Assert.Contains("confirm=true", createCalls[0].Body);
        Assert.Contains("payment_method=pm_card_visa", createCalls[0].Body);
        Assert.DoesNotContain("4242424242424242", createCalls[0].Body);
        Assert.All(http.Calls.Where(call => call.IdempotencyKey.Length > 0),
            call => Assert.StartsWith("payment-gateway:", call.IdempotencyKey));
    }

    [Fact]
    public async Task Stripe_DoesNotTreatPendingOrActionAsAuthorized()
    {
        var http = new StubStripeHttpClient(request =>
            request.Uri.AbsolutePath == "/v1/payment_intents"
                ? IntentJson("requires_action")
                : throw new InvalidOperationException());
        var processor = NewStripeProcessor(http);

        var result = await processor.AuthorizeAsync(new ProviderAuthorizeRequest(
            Guid.NewGuid(), 1000, "BRL", "pm_card_authenticationRequired", null, "action-1"));

        Assert.Equal(ProviderPaymentStatus.RequiresAction, result.Status);
        Assert.Equal("pi_test_secret_test", result.ClientSecret);
    }

    [Fact]
    public async Task Stripe_MapsCardErrorWithPaymentIntentToDeclined()
    {
        var http = new StubStripeHttpClient(_ => """
            {"error":{"type":"card_error","code":"card_declined","decline_code":"generic_decline","payment_intent":{"id":"pi_test","object":"payment_intent","status":"requires_payment_method","livemode":false,"amount":1000,"currency":"brl"}}}
            """, HttpStatusCode.PaymentRequired);
        var processor = NewStripeProcessor(http);

        var result = await processor.AuthorizeAsync(new ProviderAuthorizeRequest(
            Guid.NewGuid(), 1000, "BRL", "pm_card_visa_chargeDeclined", null, "decline-1"));

        Assert.Equal(ProviderPaymentStatus.Declined, result.Status);
        Assert.Equal("generic_decline", result.DeclineReason);
        Assert.Equal("pi_test", result.ProviderPaymentId);
    }

    [Fact]
    public async Task Stripe_RejectsLiveModeResponseEvenWithTestKey()
    {
        var http = new StubStripeHttpClient(_ => IntentJson("requires_capture")
            .Replace("\"livemode\":false", "\"livemode\":true", StringComparison.Ordinal));
        var processor = NewStripeProcessor(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.AuthorizeAsync(
            new ProviderAuthorizeRequest(Guid.NewGuid(), 1000, "BRL", "pm_card_visa", null, "live-1")));
    }

    [Fact]
    public async Task Stripe_GetReconcilesFullRefund()
    {
        var http = new StubStripeHttpClient(request => request.Uri.AbsolutePath switch
        {
            "/v1/payment_intents/pi_test" => IntentJson("succeeded"),
            "/v1/refunds" => """
                {"object":"list","data":[{"id":"re_test","object":"refund","amount":1000,"status":"succeeded","payment_intent":"pi_test"}],"has_more":false}
                """,
            _ => throw new InvalidOperationException()
        });
        var processor = NewStripeProcessor(http);

        var result = await processor.GetAsync("pi_test");

        Assert.Equal(ProviderPaymentStatus.Refunded, result.Status);
    }

    [Fact]
    public async Task Stripe_GetSumsRefundsAcrossPagesBeforeReportingFullRefund()
    {
        var http = new StubStripeHttpClient(request => request.Uri.AbsolutePath switch
        {
            "/v1/payment_intents/pi_test" => IntentJson("succeeded"),
            "/v1/refunds" when request.Uri.Query.Contains("starting_after", StringComparison.Ordinal) => """
                {"object":"list","data":[{"id":"re_second","object":"refund","amount":500,"status":"succeeded","payment_intent":"pi_test"}],"has_more":false}
                """,
            "/v1/refunds" => """
                {"object":"list","data":[{"id":"re_first","object":"refund","amount":500,"status":"succeeded","payment_intent":"pi_test"}],"has_more":true}
                """,
            _ => throw new InvalidOperationException()
        });

        var result = await NewStripeProcessor(http).GetAsync("pi_test");

        Assert.Equal(ProviderPaymentStatus.Refunded, result.Status);
        Assert.Equal(2, http.Calls.Count(call => call.Path == "/v1/refunds"));
    }

    [Fact]
    public async Task Stripe_RefundSuccessRemainsPendingUntilFullRefundIsVisible()
    {
        var http = new StubStripeHttpClient(request => request.Uri.AbsolutePath switch
        {
            "/v1/refunds" when request.Uri.Query.Length == 0 => """
                {"id":"re_test","object":"refund","amount":1000,"status":"succeeded","payment_intent":"pi_test"}
                """,
            "/v1/payment_intents/pi_test" => IntentJson("succeeded"),
            "/v1/refunds" => """{"object":"list","data":[],"has_more":false}""",
            _ => throw new InvalidOperationException()
        });

        var result = await NewStripeProcessor(http).RefundAsync("pi_test", "refund-pending");

        Assert.Equal(ProviderPaymentStatus.Pending, result.Status);
    }

    [Fact]
    public async Task Stripe_MapIntentExposesLocalIdentityAndCapturedAmount()
    {
        var paymentId = Guid.NewGuid();
        var json = IntentJson("succeeded")
            .Replace("\"amount_received\":1000", "\"amount_received\":500", StringComparison.Ordinal)
            .Replace("\"currency\":\"brl\"",
                $"\"currency\":\"brl\",\"metadata\":{{\"gateway_payment_id\":\"{paymentId:D}\"}}",
                StringComparison.Ordinal);
        var http = new StubStripeHttpClient(request => request.Uri.AbsolutePath switch
        {
            "/v1/payment_intents/pi_test" => json,
            "/v1/refunds" => """{"object":"list","data":[],"has_more":false}""",
            _ => throw new InvalidOperationException()
        });

        var result = await NewStripeProcessor(http).GetAsync("pi_test");

        Assert.Equal(paymentId, result.GatewayPaymentId);
        Assert.Equal(500, result.AmountInCents);
        Assert.Equal("brl", result.Currency);
    }

    private static StripePaymentProcessor NewStripeProcessor(IHttpClient http) =>
        new(new StripeClient("sk_test_unit", httpClient: http));

    private static string IntentJson(string status) => """
        {"id":"pi_test","object":"payment_intent","status":"__STATUS__","livemode":false,"amount":1000,"amount_received":1000,"currency":"brl","client_secret":"pi_test_secret_test","payment_method":{"id":"pm_card_visa","object":"payment_method","type":"card","card":{"brand":"visa","last4":"4242"}}}
        """.Replace("__STATUS__", status, StringComparison.Ordinal);

    private sealed class StubStripeHttpClient(
        Func<StripeRequest, string> respond, HttpStatusCode statusCode = HttpStatusCode.OK) : IHttpClient
    {
        public List<(string Path, string IdempotencyKey, string Body)> Calls { get; } = [];

        public async Task<StripeResponse> MakeRequestAsync(
            StripeRequest request, CancellationToken cancellationToken = default)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.StripeHeaders.TryGetValue("Idempotency-Key", out var idempotencyKey);
            Calls.Add((request.Uri.AbsolutePath, idempotencyKey ?? string.Empty, body));
            var response = new HttpResponseMessage();
            return new StripeResponse(statusCode, response.Headers, respond(request));
        }

        public Task<StripeStreamedResponse> MakeStreamingRequestAsync(
            StripeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
