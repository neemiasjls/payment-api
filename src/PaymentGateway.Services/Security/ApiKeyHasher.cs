using System.Security.Cryptography;
using System.Text;

namespace PaymentGateway.Services.Security;

/// <summary>
/// A API key funciona como senha do lojista, então nunca é armazenada em texto
/// puro. Guardamos o SHA-256 e comparamos hashes na autenticação — se o banco
/// vazar, as chaves não vazam junto.
/// </summary>
public static class ApiKeyHasher
{
    public static string GenerateApiKey()
    {
        // 32 bytes aleatórios criptograficamente seguros, prefixo "pk_" no
        // estilo dos gateways reais (facilita identificar a chave em logs).
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "pk_" + Convert.ToHexStringLower(bytes);
    }

    public static string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexStringLower(bytes);
    }
}
