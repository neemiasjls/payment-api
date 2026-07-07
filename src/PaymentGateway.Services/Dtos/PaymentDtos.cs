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

    [Required]
    public CardRequest Card { get; set; } = null!;
}

public class CardRequest
{
    [Required]
    public string Number { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string HolderName { get; set; } = string.Empty;

    [Range(1, 12)]
    public int ExpMonth { get; set; }

    [Range(2000, 2100)]
    public int ExpYear { get; set; }

    // O CVV é recebido para simular a autorização, mas jamais persistido:
    // armazená-lo é proibido pelo PCI DSS mesmo criptografado.
    [Required, StringLength(4, MinimumLength = 3)]
    public string Cvv { get; set; } = string.Empty;
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
    DateTime? RefundedAtUtc)
{
    public static PaymentResponse FromEntity(Payment payment) => new(
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
        payment.RefundedAtUtc);
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
