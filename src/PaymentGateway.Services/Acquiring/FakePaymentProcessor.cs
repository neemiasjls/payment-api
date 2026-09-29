using System.Collections.Concurrent;
using PaymentGateway.Domain.Exceptions;

namespace PaymentGateway.Services.Acquiring;

/// <summary>An in-memory processor for local development and deterministic tests.</summary>
public sealed class FakePaymentProcessor : IPaymentProcessor
{
    private readonly ConcurrentDictionary<string, FakePayment> _payments = new(StringComparer.Ordinal);

    public string Name => "fake";

    public Task<ProviderPaymentResult> AuthorizeAsync(
        ProviderAuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.PaymentId == Guid.Empty ||
            request.AmountInCents is < 50 or > 99_999_999 ||
            string.IsNullOrWhiteSpace(request.Currency) || string.IsNullOrWhiteSpace(request.OperationKey))
            throw new DomainException("Parâmetros de autorização inválidos.");

        var card = TestPaymentMethods.GetCard(request.PaymentMethodId);
        var providerId = $"fake_pi_{request.PaymentId:N}";
        var initialStatus = request.PaymentMethodId == "pm_card_authenticationRequired"
            ? ProviderPaymentStatus.RequiresAction
            : AcquirerSimulator.Authorize(request.PaymentMethodId) is not null
                ? ProviderPaymentStatus.Declined
                : ProviderPaymentStatus.Authorized;

        var payment = _payments.GetOrAdd(providerId, _ => new FakePayment(
            request, new ProviderPaymentResult(
                providerId,
                initialStatus,
                card.Last4,
                card.Brand,
                AcquirerSimulator.Authorize(request.PaymentMethodId),
                initialStatus == ProviderPaymentStatus.RequiresAction ? $"fake_secret_{request.PaymentId:N}" : null,
                request.PaymentId,
                request.AmountInCents,
                request.Currency)));

        lock (payment.Gate)
        {
            if (payment.Request != request)
                throw new DomainException("Conflito de idempotência na autorização simulada.");

            return Task.FromResult(payment.AuthorizationResult);
        }
    }

    public Task<ProviderPaymentResult> CaptureAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default) =>
        TransitionAsync(providerPaymentId, operationKey, ProviderPaymentStatus.Authorized,
            ProviderPaymentStatus.Captured, cancellationToken);

    public Task<ProviderPaymentResult> VoidAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default) =>
        TransitionAsync(providerPaymentId, operationKey, ProviderPaymentStatus.Authorized,
            ProviderPaymentStatus.Voided, cancellationToken);

    public Task<ProviderPaymentResult> RefundAsync(
        string providerPaymentId, string operationKey, CancellationToken cancellationToken = default) =>
        TransitionAsync(providerPaymentId, operationKey, ProviderPaymentStatus.Captured,
            ProviderPaymentStatus.Refunded, cancellationToken);

    public Task<ProviderPaymentResult> GetAsync(
        string providerPaymentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payment = Load(providerPaymentId);
        lock (payment.Gate)
            return Task.FromResult(payment.Result);
    }

    private Task<ProviderPaymentResult> TransitionAsync(
        string providerPaymentId, string operationKey, ProviderPaymentStatus expected,
        ProviderPaymentStatus target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new DomainException("A chave de idempotência é obrigatória.");

        var payment = Load(providerPaymentId);
        lock (payment.Gate)
        {
            if (payment.CompletedOperations.TryGetValue(operationKey, out var prior))
            {
                if (prior.Status != target)
                    throw new DomainException("Chave de idempotência reutilizada para outra operação.");
                return Task.FromResult(prior);
            }

            if (payment.Result.Status != expected)
                throw new DomainException($"Operação inválida para pagamento {payment.Result.Status}.");

            payment.Result = payment.Result with { Status = target, ClientSecret = null };
            payment.CompletedOperations.Add(operationKey, payment.Result);
            return Task.FromResult(payment.Result);
        }
    }

    private FakePayment Load(string providerPaymentId) =>
        _payments.TryGetValue(providerPaymentId, out var payment)
            ? payment
            : throw new DomainException("Pagamento simulado não encontrado.");

    private sealed class FakePayment(ProviderAuthorizeRequest request, ProviderPaymentResult result)
    {
        public object Gate { get; } = new();
        public ProviderAuthorizeRequest Request { get; } = request;
        public ProviderPaymentResult AuthorizationResult { get; } = result;
        public ProviderPaymentResult Result { get; set; } = result;
        public Dictionary<string, ProviderPaymentResult> CompletedOperations { get; } = new(StringComparer.Ordinal);
    }
}
