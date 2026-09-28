namespace PaymentGateway.Api.Middleware;

/// <summary>
/// Adiciona cabeçalhos de segurança a todas as respostas. São defesas baratas
/// que instruem o cliente (navegador, proxy) a não interpretar, embutir nem
/// guardar em cache respostas com dados de pagamento.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Impede o navegador de "adivinhar" o tipo do conteúdo (MIME sniffing).
        headers.XContentTypeOptions = "nosniff";

        // Impede que a API seja embutida em iframe (clickjacking).
        headers.XFrameOptions = "DENY";

        // Não envia a URL de origem para outros sites.
        headers["Referrer-Policy"] = "no-referrer";

        // O Swagger UI precisa carregar scripts e estilos; as demais rotas só
        // devolvem JSON, então recebem a política mais restritiva possível.
        if (!context.Request.Path.StartsWithSegments("/swagger"))
        {
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

            // Respostas com dados de pagamento nunca devem ficar em cache.
            headers.CacheControl = "no-store";
        }

        return _next(context);
    }
}
