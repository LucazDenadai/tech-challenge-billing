using System.Diagnostics;

namespace OficinaMecanica.Billing.API.Middleware;

// Correlation ID das requisições HTTP: reaproveita X-Correlation-Id válido ou gera um novo,
// devolve no cabeçalho de resposta e o coloca no escopo de log e no trace.
public class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Cabecalho = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Guid.TryParse(context.Request.Headers[Cabecalho], out var recebido)
            ? recebido
            : Guid.NewGuid();

        context.Response.Headers[Cabecalho] = correlationId.ToString();
        Activity.Current?.SetTag("correlation_id", correlationId.ToString());

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }
}
