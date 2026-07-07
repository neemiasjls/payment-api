namespace PaymentGateway.Domain.Exceptions;

/// <summary>
/// Violação de regra de negócio (ex.: capturar um pagamento já estornado).
/// A API converte esta exceção em HTTP 422.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
