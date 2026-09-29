using System.ComponentModel.DataAnnotations;
using PaymentGateway.Domain.Entities;

namespace PaymentGateway.Services.Dtos;

public class CreatePaymentRequest
{
    /// <summary>Valor em centavos: R$ 10,00 = 1000.</summary>
    [Range(1, long.MaxValue, ErrorMessage = "O valor deve ser maior que zero.")]
    public long AmountInCents { get; set; }

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "BRL";

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>Identificador de PaymentMethod gerado no ambiente de testes do provedor.</summary>
    [Required, StringLength(200, MinimumLength = 4)]
    public string PaymentMethodId { get; set; } = string.Empty;
}

public record PaymentResponse(
    Guid Id,
    long AmountInCents,
    string Currency,
    string Status,
    string CardLast4,
    string CardBrand,
    string? Description,
    string? DeclineReason,
    DateTime CreatedAtUtc,
    DateTime? CapturedAtUtc,
    DateTime? VoidedAtUtc,
    DateTime? RefundedAtUtc,
    string Provider,
    string? ProviderPaymentId,
    string? PendingOperation,
    string? ClientSecret)
{
    public static PaymentResponse FromEntity(Payment payment, string? clientSecret = null) => new(
        payment.Id,
        payment.AmountInCents,
        payment.Currency,
        payment.Status.ToString(),
        payment.CardLast4,
        payment.CardBrand,
        payment.Description,
        payment.DeclineReason,
        payment.CreatedAtUtc,
        payment.CapturedAtUtc,
        payment.VoidedAtUtc,
        payment.RefundedAtUtc,
        payment.Provider,
        payment.ProviderPaymentId,
        payment.PendingOperation,
        clientSecret);
}

public record PaymentEventResponse(
    Guid Id, string Type, DateTime CreatedAtUtc, int DeliveryAttempts, DateTime? DeliveredAtUtc)
{
    public static PaymentEventResponse FromEntity(PaymentEvent paymentEvent) => new(
        paymentEvent.Id,
        paymentEvent.Type,
        paymentEvent.CreatedAtUtc,
        paymentEvent.DeliveryAttempts,
        paymentEvent.DeliveredAtUtc);
}

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
