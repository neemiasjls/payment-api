using Microsoft.AspNetCore.Mvc;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Domain.Enums;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Api.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _payments;

    public PaymentsController(IPaymentService payments)
    {
        _payments = payments;
    }

    /// <summary>
    /// Autoriza um novo pagamento. Envie o header Idempotency-Key para
    /// garantir que retentativas não gerem cobrança duplicada.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        var (payment, created) = await _payments.AuthorizeAsync(merchant.Id, request, idempotencyKey, ct);

        // 201 para pagamento novo; 200 quando a idempotência devolve um existente.
        return created
            ? CreatedAtAction(nameof(GetById), new { id = payment.Id }, payment)
            : Ok(payment);
    }

    /// <summary>Lista os pagamentos do lojista, com paginação e filtro por status.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<PaymentResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] PaymentStatus? status = null,
        CancellationToken ct = default)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _payments.ListAsync(merchant.Id, page, pageSize, status, ct));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _payments.GetAsync(merchant.Id, id, ct));
    }

    /// <summary>Captura (confirma a cobrança de) um pagamento autorizado.</summary>
    [HttpPost("{id:guid}/capture")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Capture(Guid id, CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _payments.CaptureAsync(merchant.Id, id, ct));
    }

    /// <summary>Cancela uma autorização ainda não capturada.</summary>
    [HttpPost("{id:guid}/void")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> VoidPayment(Guid id, CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _payments.VoidAsync(merchant.Id, id, ct));
    }

    /// <summary>Estorna um pagamento capturado.</summary>
    [HttpPost("{id:guid}/refund")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Refund(Guid id, CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _payments.RefundAsync(merchant.Id, id, ct));
    }

    /// <summary>Trilha de eventos do pagamento (auditoria + status dos webhooks).</summary>
    [HttpGet("{id:guid}/events")]
    [ProducesResponseType<IReadOnlyList<PaymentEventResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvents(Guid id, CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _payments.GetEventsAsync(merchant.Id, id, ct));
    }
}
