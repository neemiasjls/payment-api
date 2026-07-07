using PaymentGateway.Domain.Cards;

namespace PaymentGateway.Tests;

public class CardValidatorTests
{
    [Theory]
    [InlineData("4242424242424242")] // Visa de teste
    [InlineData("5555555555554444")] // Mastercard de teste
    [InlineData("4242 4242 4242 4242")] // com espaços
    [InlineData("371449635398431")] // Amex (15 dígitos)
    public void IsValidLuhn_ComNumerosValidos_RetornaTrue(string cardNumber)
    {
        Assert.True(CardValidator.IsValidLuhn(cardNumber));
    }

    [Theory]
    [InlineData("4242424242424241")] // dígito verificador errado
    [InlineData("1234567890123456")]
    [InlineData("")]
    [InlineData("abcd4242efgh4242")]
    [InlineData("4242")] // curto demais
    public void IsValidLuhn_ComNumerosInvalidos_RetornaFalse(string cardNumber)
    {
        Assert.False(CardValidator.IsValidLuhn(cardNumber));
    }

    [Theory]
    [InlineData("4242424242424242", "Visa")]
    [InlineData("5555555555554444", "Mastercard")]
    [InlineData("371449635398431", "Amex")]
    [InlineData("6363680000457013", "Elo")]
    [InlineData("6062825624254001", "Hipercard")]
    public void DetectBrand_IdentificaABandeiraCorreta(string cardNumber, string expectedBrand)
    {
        Assert.Equal(expectedBrand, CardValidator.DetectBrand(cardNumber));
    }

    [Fact]
    public void IsExpired_CartaoVencidoNoMesPassado_RetornaTrue()
    {
        var now = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(CardValidator.IsExpired(6, 2026, now));
    }

    [Fact]
    public void IsExpired_CartaoValeAteOFimDoMesDeExpiracao_RetornaFalse()
    {
        var now = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(CardValidator.IsExpired(7, 2026, now));
    }

    [Fact]
    public void IsExpired_MesInvalido_RetornaTrue()
    {
        var now = DateTime.UtcNow;
        Assert.True(CardValidator.IsExpired(13, 2030, now));
    }

    [Fact]
    public void GetLast4_RetornaOsQuatroUltimosDigitos()
    {
        Assert.Equal("4242", CardValidator.GetLast4("4242 4242 4242 4242"));
    }
}
