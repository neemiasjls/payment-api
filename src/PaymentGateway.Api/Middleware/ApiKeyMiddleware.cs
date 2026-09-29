using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using PaymentGateway.Services;

namespace PaymentGateway.Api.Middleware;

/// <summary>
/// Autenticação por API key (header X-Api-Key), o padrão em gateways de
/// pagamento para comunicação servidor-a-servidor. O lojista autenticado
/// fica disponível em HttpContext.Items para os controllers.
/// </summary>
public class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";
    public const string MerchantItemKey = "Merchant";

    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;

    public ApiKeyMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        _next = next;
        _environment = environment;
    }

    // IMerchantService é scoped, então é injetado no Invoke (por requisição),
    // não no construtor do middleware (que é singleton).
    public async Task InvokeAsync(HttpContext context, IMerchantService merchants)
    {
        if (IsPublicEndpoint(context))
        {
            await _next(context);
            return;
        }

        var apiKey = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await WriteUnauthorizedAsync(context, $"Informe sua API key no header {HeaderName}.");
            return;
        }

        var merchant = await merchants.FindByApiKeyAsync(apiKey, context.RequestAborted);

        if (merchant is null)
        {
            await WriteUnauthorizedAsync(context, "API key inválida.");
            return;
        }

        context.Items[MerchantItemKey] = merchant;
        await _next(context);
    }

    private bool IsPublicEndpoint(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/swagger") || path.StartsWithSegments("/health") || path == "/")
            return true;

        if (!HttpMethods.IsPost(context.Request.Method))
            return false;

        var route = path.Value?.TrimEnd('/');

        // A Stripe autentica este endpoint com a assinatura do payload bruto.
        if (string.Equals(route, "/api/v1/webhooks/stripe", StringComparison.OrdinalIgnoreCase))
            return true;

        // Cadastro anônimo é apenas uma conveniência do portfólio local.
        return (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
               && string.Equals(route, "/api/v1/merchants", StringComparison.OrdinalIgnoreCase);
    }

    private static Task WriteUnauthorizedAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Não autorizado",
            Detail = detail,
            Instance = context.Request.Path
        });
    }
}
