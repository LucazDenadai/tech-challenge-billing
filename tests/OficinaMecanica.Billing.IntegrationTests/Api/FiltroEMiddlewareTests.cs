using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.API.Extensions;
using OficinaMecanica.Billing.API.Filters;
using OficinaMecanica.Billing.API.Middleware;
using OficinaMecanica.Billing.Application.Exceptions;

namespace OficinaMecanica.Billing.IntegrationTests.Api;

// Filtro de exceções, middleware de correlação e configuração da telemetria, sem subir a API.
public class FiltroEMiddlewareTests
{
    public static TheoryData<Exception, int> Excecoes() => new()
    {
        { new NotFoundException("Orçamento", Guid.NewGuid()), StatusCodes.Status404NotFound },
        { new KeyNotFoundException("Chave ausente"), StatusCodes.Status404NotFound },
        { new ConflitoConcorrenciaException(new Exception("versão")), StatusCodes.Status409Conflict },
        { new InvalidOperationException("Transição inválida"), StatusCodes.Status422UnprocessableEntity },
        { new ArgumentException("Versão inválida"), StatusCodes.Status400BadRequest },
        { new TimeoutException("Banco lento"), StatusCodes.Status500InternalServerError }
    };

    [Theory]
    [MemberData(nameof(Excecoes))]
    public void Filtro_MapeiaExcecaoParaStatus(Exception excecao, int status)
    {
        var contexto = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = excecao
        };

        new GlobalExceptionFilter(NullLogger<GlobalExceptionFilter>.Instance).OnException(contexto);

        var resultado = Assert.IsType<ObjectResult>(contexto.Result);
        var problema = Assert.IsType<ProblemDetails>(resultado.Value);
        Assert.True(contexto.ExceptionHandled);
        Assert.Equal(status, resultado.StatusCode);
        // Erro inesperado não devolve a mensagem interna.
        Assert.Equal(status == StatusCodes.Status500InternalServerError ? null : excecao.Message, problema.Detail);
    }

    [Fact]
    public async Task Middleware_SemTraceAtivo_SoDevolveOCabecalho()
    {
        Activity.Current = null;
        var contexto = new DefaultHttpContext();
        var chamou = false;

        await new CorrelationIdMiddleware(_ => { chamou = true; return Task.CompletedTask; }, NullLogger<CorrelationIdMiddleware>.Instance)
            .InvokeAsync(contexto);

        Assert.True(chamou);
        Assert.True(Guid.TryParse(contexto.Response.Headers[CorrelationIdMiddleware.Cabecalho], out _));
    }

    [Fact]
    public void CaminhoVazio_ViraTrace()
        => Assert.True(AmostradorInicioDeFluxo.DeveRastrear(default));

    [Fact]
    public void Telemetria_SemEndpointDoJaeger_NaoSobe()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Configuration = new ConfigurationManager() });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddOpenTelemetry("billing"));

        Assert.Contains("Jaeger:Endpoint", ex.Message);
    }
}
