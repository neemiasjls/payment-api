using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PaymentGateway.Services;
using PaymentGateway.Services.Acquiring;
using Stripe;

namespace PaymentGateway.Api.Controllers;

/// <summary>Recebe eventos assinados da conta Stripe Test Mode.</summary>
[ApiController]
[Route("api/v1/webhooks/stripe")]
public sealed class StripeWebhooksController : ControllerBase
{
    private readonly IPaymentService _payments;
    private readonly IPaymentProcessor _processor;
    private readonly StripeProcessorOptions _options;

    public StripeWebhooksController(
        IPaymentService payments, IPaymentProcessor processor,
        IOptions<StripeProcessorOptions> options)
    {
        _payments = payments;
        _processor = processor;
        _options = options.Value;
    }

    [HttpPost]
    [RequestSizeLimit(65_536)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (_processor.Name != "stripe" || string.IsNullOrWhiteSpace(_options.WebhookSecret))
            return NotFound();

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var signature = Request.Headers["Stripe-Signature"].ToString();
        try
        {
            // A assinatura usa os bytes do corpo original, sem reserialização.
            EventUtility.ValidateSignature(body, signature, _options.WebhookSecret, 300);
        }
        catch (StripeException)
        {
            return Unauthorized();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return BadRequest();
        }
        using var parsedDocument = document;
        var root = document.RootElement;
        if (!root.TryGetProperty("livemode", out var liveMode) || liveMode.ValueKind != JsonValueKind.False)
            return BadRequest();
        if (!root.TryGetProperty("id", out var idElement) ||
            !root.TryGetProperty("type", out var typeElement) ||
            !root.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("object", out var objectElement))
            return BadRequest();

        if (idElement.ValueKind != JsonValueKind.String ||
            typeElement.ValueKind != JsonValueKind.String)
            return BadRequest();
        var eventId = idElement.GetString();
        var eventType = typeElement.GetString();
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType))
            return BadRequest();

        string? providerPaymentId = null;
        Guid? localPaymentId = null;
        if (eventType.StartsWith("payment_intent.", StringComparison.Ordinal) &&
            objectElement.TryGetProperty("id", out var intentId))
        {
            if (intentId.ValueKind != JsonValueKind.String)
                return BadRequest();
            providerPaymentId = intentId.GetString();
            if (objectElement.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("gateway_payment_id", out var localId) &&
                localId.ValueKind == JsonValueKind.String &&
                Guid.TryParse(localId.GetString(), out var parsedId))
                localPaymentId = parsedId;
        }
        else if ((eventType.StartsWith("refund.", StringComparison.Ordinal) ||
                  eventType == "charge.refunded") &&
                 objectElement.TryGetProperty("payment_intent", out var paymentIntent))
        {
            if (paymentIntent.ValueKind != JsonValueKind.String)
                return BadRequest();
            providerPaymentId = paymentIntent.GetString();
        }

        if (!string.IsNullOrWhiteSpace(providerPaymentId))
            await _payments.ReconcileProviderEventAsync(eventId, providerPaymentId, localPaymentId, ct);

        return Ok();
    }
}
