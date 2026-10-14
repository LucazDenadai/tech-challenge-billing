using System.Diagnostics;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OficinaMecanica.Billing.API.Extensions;

public static class OpenTelemetryExtensions
{
    public static IHostApplicationBuilder AddOpenTelemetry(
        this IHostApplicationBuilder builder, string serviceName)
    {
        var jaegerEndpoint = builder.Configuration["Jaeger:Endpoint"] ?? "http://jaeger:4318";
        // Versão da imagem (sha-<commit>), gravada no build; "local" fora do CI.
        var versao = builder.Configuration["Servico:Versao"] ?? "local";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(recurso => recurso
                .AddService(serviceName, serviceNamespace: "oficina-mecanica", serviceVersion: versao)
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new AmostradorInicioDeFluxo()))
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.RecordException = true;
                    o.Filter = contexto => AmostradorInicioDeFluxo.DeveRastrear(contexto.Request.Path);
                })
                .AddEntityFrameworkCoreInstrumentation()
                // Chamada ao Mercado Pago como span filho da Saga (CARD-42a). A instrumentação não grava corpo nem
                // cabeçalhos, e a query string sai redigida; o token vai no cabeçalho Authorization.
                .AddHttpClientInstrumentation(o => o.RecordException = true)
                .AddSource(TelemetriaMensageria.NomeFonte)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri($"{jaegerEndpoint.TrimEnd('/')}/v1/traces");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                }))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(TelemetriaMensageria.NomeFonte)
                .AddMeter(MetricasPagamento.NomeMedidor)
                .AddPrometheusExporter());

        return builder;
    }
}

// Só começa um trace o que começa um fluxo: requisição HTTP recebida, mensagem consumida ou publicada (CARD-42a).
// Um SQL ou chamada sem pai (laço da outbox, reconciliação de pagamentos) não vira trace próprio; dentro de um fluxo, continua como
// span filho, porque o ParentBasedSampler segue a decisão do pai.
public sealed class AmostradorInicioDeFluxo : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        => new(samplingParameters.Kind is ActivityKind.Server or ActivityKind.Consumer or ActivityKind.Producer
            ? SamplingDecision.RecordAndSample
            : SamplingDecision.Drop);

    // Probes do Kubernetes e scrape do Prometheus não são fluxo de negócio.
    public static bool DeveRastrear(PathString caminho)
        => !(caminho.Value?.EndsWith("/health", StringComparison.OrdinalIgnoreCase) == true
             || caminho.Value?.EndsWith("/ready", StringComparison.OrdinalIgnoreCase) == true
             || caminho.StartsWithSegments("/metrics"));
}
