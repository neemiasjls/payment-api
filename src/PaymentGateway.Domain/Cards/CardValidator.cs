namespace PaymentGateway.Domain.Cards;

/// <summary>
/// Validações de cartão feitas no gateway antes de acionar o "emissor":
/// algoritmo de Luhn, detecção de bandeira e checagem de validade.
/// </summary>
public static class CardValidator
{
    /// <summary>
    /// Algoritmo de Luhn (módulo 10): detecta erros de digitação no número
    /// do cartão. Todo cartão real passa por esta checagem.
    /// </summary>
    public static bool IsValidLuhn(string cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
            return false;

        var digits = cardNumber.Replace(" ", "").Replace("-", "");
        if (digits.Length < 13 || digits.Length > 19 || !digits.All(char.IsDigit))
            return false;

        var sum = 0;
        var doubleDigit = false;

        // Percorre da direita para a esquerda dobrando dígitos alternados.
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';

            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// Detecta a bandeira pelo BIN (prefixo do número). Inclui bandeiras
    /// brasileiras (Elo, Hipercard), que têm prefixos próprios.
    /// </summary>
    public static string DetectBrand(string cardNumber)
    {
        var digits = cardNumber.Replace(" ", "").Replace("-", "");

        if (digits.Length < 4)
            return "Unknown";

        // Bandeiras brasileiras primeiro: alguns BINs da Elo começam com 4/5
        // e seriam confundidos com Visa/Mastercard.
        string[] eloPrefixes = ["401178", "438935", "504175", "506699", "636368", "650031"];
        if (eloPrefixes.Any(digits.StartsWith))
            return "Elo";

        if (digits.StartsWith("6062"))
            return "Hipercard";

        if (digits.StartsWith('4'))
            return "Visa";

        if (digits.Length >= 2 && int.TryParse(digits[..2], out var twoDigits))
        {
            if (twoDigits is >= 51 and <= 55)
                return "Mastercard";

            if (twoDigits is 34 or 37)
                return "Amex";
        }

        return "Unknown";
    }

    public static bool IsExpired(int expMonth, int expYear, DateTime utcNow)
    {
        if (expMonth is < 1 or > 12)
            return true;

        // Cartão vale até o último dia do mês de expiração.
        var lastDayOfMonth = new DateTime(expYear, expMonth, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(1)
            .AddTicks(-1);

        return utcNow > lastDayOfMonth;
    }

    public static string GetLast4(string cardNumber)
    {
        var digits = cardNumber.Replace(" ", "").Replace("-", "");
        return digits.Length >= 4 ? digits[^4..] : digits;
    }
}
