using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OficinaMecanica.Billing.Application.Exceptions;

namespace OficinaMecanica.Billing.API.Filters;

public class GlobalExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GlobalExceptionFilter> _logger;

    public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        var (statusCode, title) = context.Exception switch
        {
            NotFoundException or KeyNotFoundException => (StatusCodes.Status404NotFound, "Recurso não encontrado"),
            ConflitoConcorrenciaException => (StatusCodes.Status409Conflict, "Conflito de concorrência"),
            InvalidOperationException => (StatusCodes.Status422UnprocessableEntity, "Operação inválida"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Requisição inválida"),
            _ => (StatusCodes.Status500InternalServerError, "Erro interno do servidor")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            _logger.LogError(context.Exception, "Exceção não tratada: {Message}", context.Exception.Message);
        else
            _logger.LogInformation("Requisição recusada com {StatusCode}: {Message}", statusCode, context.Exception.Message);

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            // Erro inesperado não devolve a mensagem interna ao cliente.
            Detail = statusCode == StatusCodes.Status500InternalServerError ? null : context.Exception.Message
        })
        { StatusCode = statusCode };

        context.ExceptionHandled = true;
    }
}
