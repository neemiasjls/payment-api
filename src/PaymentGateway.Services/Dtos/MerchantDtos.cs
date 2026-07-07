using System.ComponentModel.DataAnnotations;
using PaymentGateway.Domain.Entities;

namespace PaymentGateway.Services.Dtos;

public class CreateMerchantRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [Url, StringLength(500)]
    public string? WebhookUrl { get; set; }
}

/// <summary>
/// Resposta do cadastro: única vez em que a API key aparece em texto puro.
/// Depois disso, só o hash existe no banco.
/// </summary>
public record MerchantCreatedResponse(Guid Id, string Name, string Email, string ApiKey);

public record MerchantResponse(Guid Id, string Name, string Email, string? WebhookUrl, DateTime CreatedAtUtc)
{
    public static MerchantResponse FromEntity(Merchant merchant) =>
        new(merchant.Id, merchant.Name, merchant.Email, merchant.WebhookUrl, merchant.CreatedAtUtc);
}

public record BalanceResponse(Guid MerchantId, long AvailableInCents, string Currency);
