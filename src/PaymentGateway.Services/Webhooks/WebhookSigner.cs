using System.Security.Cryptography;
using System.Text;

namespace PaymentGateway.Services.Webhooks;

/// <summary>
/// Assina o corpo dos webhooks com HMAC-SHA256, como fazem Stripe e GitHub.
/// O lojista recalcula o HMAC com o segredo dele e compara com o header
/// X-Webhook-Signature: se bater, a notificação é autêntica e íntegra —
/// ninguém além do gateway (que conhece o segredo) conseguiria gerá-la.
/// </summary>
public static class WebhookSigner
{
    public static string GenerateSecret() =>
        "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string payload, string secret)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));

        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
