namespace PaymentGateway.Domain.Exceptions;

/// <summary>Recurso não encontrado (ou não pertence ao lojista autenticado). Vira HTTP 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }
}
