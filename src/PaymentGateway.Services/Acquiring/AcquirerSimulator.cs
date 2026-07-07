namespace PaymentGateway.Services.Acquiring;

/// <summary>
/// Simula a resposta da rede de adquirência/emissor do cartão.
/// Em um gateway real, aqui haveria a integração com a bandeira (Visa,
/// Mastercard...) e o banco emissor. Usamos "cartões de teste" com
/// comportamento fixo, no mesmo estilo do ambiente sandbox da Stripe.
/// </summary>
public static class AcquirerSimulator
{
    private static readonly Dictionary<string, string> DeclineRules = new()
    {
        ["4000000000000002"] = "card_declined",
        ["4000000000009995"] = "insufficient_funds",
        ["4000000000009987"] = "lost_card"
    };

    /// <summary>Retorna null se aprovado, ou o motivo da recusa.</summary>
    public static string? Authorize(string cardNumber)
    {
        var digits = cardNumber.Replace(" ", "").Replace("-", "");
        return DeclineRules.GetValueOrDefault(digits);
    }
}
