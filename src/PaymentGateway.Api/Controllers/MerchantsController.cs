using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using PaymentGateway.Api.Middleware;
using PaymentGateway.Services;
using PaymentGateway.Services.Dtos;

namespace PaymentGateway.Api.Controllers;

[ApiController]
[Route("api/v1/merchants")]
public class MerchantsController : ControllerBase
{
    private readonly IMerchantService _merchants;
    private readonly IHostEnvironment _environment;

    public MerchantsController(IMerchantService merchants, IHostEnvironment environment)
    {
        _merchants = merchants;
        _environment = environment;
    }

    /// <summary>Cadastra um lojista e devolve a API key (exibida uma única vez).</summary>
    [HttpPost]
    [ProducesResponseType<MerchantCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Register(
        [FromBody] CreateMerchantRequest request, CancellationToken ct)
    {
        if (!_environment.IsDevelopment() && !_environment.IsEnvironment("Testing"))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Cadastro de lojistas indisponível",
                Detail = "O cadastro público é permitido apenas no ambiente de demonstração."
            });

        var response = await _merchants.RegisterAsync(request, ct);
        return Created("/api/v1/merchants/me", response);
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
