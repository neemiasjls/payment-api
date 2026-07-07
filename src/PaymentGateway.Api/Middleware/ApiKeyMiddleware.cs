using Microsoft.AspNetCore.Mvc;
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

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
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

    private static bool IsPublicEndpoint(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/swagger") || path.StartsWithSegments("/health") || path == "/")
            return true;

        // Único endpoint de negócio aberto: o cadastro de lojista,
        // que é justamente onde a API key é criada.
        return HttpMethods.IsPost(context.Request.Method)
               && string.Equals(path.Value?.TrimEnd('/'), "/api/v1/merchants", StringComparison.OrdinalIgnoreCase);
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
