using Microsoft.AspNetCore.Mvc;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Api.Controllers;

[ApiController]
[Route("api/merchants")]
public class MerchantsController : ControllerBase
{
    private readonly IMerchantService _merchants;

    public MerchantsController(IMerchantService merchants)
    {
        _merchants = merchants;
    }

    /// <summary>Cadastra um lojista e devolve a API key (exibida uma única vez).</summary>
    [HttpPost]
    [ProducesResponseType<MerchantCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Register(
        [FromBody] CreateMerchantRequest request, CancellationToken ct)
    {
        var response = await _merchants.RegisterAsync(request, ct);
        return Created("/api/merchants/me", response);
    }

    /// <summary>Dados do lojista autenticado.</summary>
    [HttpGet("me")]
    [ProducesResponseType<MerchantResponse>(StatusCodes.Status200OK)]
    public IActionResult GetMe() =>
        Ok(MerchantResponse.FromEntity(HttpContext.GetMerchant()));

    /// <summary>Saldo do lojista, calculado a partir do ledger.</summary>
    [HttpGet("me/balance")]
    [ProducesResponseType<BalanceResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBalance(CancellationToken ct)
    {
        var merchant = HttpContext.GetMerchant();
        return Ok(await _merchants.GetBalanceAsync(merchant.Id, ct));
    }
}
