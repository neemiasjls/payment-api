using Microsoft.AspNetCore.Mvc;
using PaymentGateway.Domain.Exceptions;
using Stripe;

namespace PaymentGateway.Api.Middleware;

/// <summary>
/// Converte exceções em respostas HTTP padronizadas (RFC 7807 - Problem Details).
/// Centralizar aqui evita try/catch espalhado pelos controllers.
/// </summary>
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (NotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "Recurso não encontrado", ex.Message);
        }
        catch (DomainException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "Regra de negócio violada", ex.Message);
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "card_error")
        {
            _logger.LogWarning(ex, "Operação recusada pela Stripe em {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity,
                "Operação recusada pelo provedor",
                ex.StripeError.DeclineCode ?? ex.StripeError.Code ?? "card_declined");
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Falha da Stripe em {Path}", context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                "Falha no provedor", "Não foi possível confirmar a operação. Consulte o pagamento antes de tentar novamente.");
        }
        catch (Exception ex)
        {
            // Detalhes internos ficam no log; o cliente recebe mensagem genérica
            // para não vazar informação da infraestrutura.
            _logger.LogError(ex, "Erro não tratado em {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError,
                "Erro interno", "Ocorreu um erro inesperado. Tente novamente mais tarde.");
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        context.Response.StatusCode = statusCode;

        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        });
    }
}
