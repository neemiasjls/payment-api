using PaymentGateway.Domain.Entities;

namespace PaymentGateway.Api.Middleware;

public static class HttpContextExtensions
{
    /// <summary>
    /// Lojista autenticado na requisição atual. Só é chamado em rotas
    /// protegidas, onde o ApiKeyMiddleware já garantiu a presença dele.
    /// </summary>
    public static Merchant GetMerchant(this HttpContext context) =>
        context.Items[ApiKeyMiddleware.MerchantItemKey] as Merchant
        ?? throw new InvalidOperationException("Rota protegida acessada sem lojista autenticado.");
}
