using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;

// Consome os comandos da Saga cujo consumidor é Billing (ADR-018): fila billing.<canal> ligada à exchange
// fanout do canal e DLQ billing.<canal>.dlq. O efeito e o evento de resultado são gravados juntos.
public class ConsumidorSagaHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    EstadoConsumidorSaga estado,
    ILogger<ConsumidorSagaHostedService> logger) : BackgroundService
{
    // Política de mensagem transitória do ADR-017: 3 novas tentativas (1 s, 5 s, 10 s), depois DLQ.
    private static readonly TimeSpan[] _esperas = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private static readonly TimeSpan _esperaReconexao = TimeSpan.FromSeconds(5);

    private readonly RabbitMqOptions _opcoes = options.Value;
    private IConnection? _conexao;
    private IChannel? _canal;

    public static string Fila(CanalConsumido canal) => $"billing.{canal.Endereco}";
    public static string Dlq(CanalConsumido canal) => $"{Fila(canal)}.dlq";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConectarAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                estado.Marcar(false);
                logger.LogWarning(ex, "Falha ao conectar ao RabbitMQ em {Host}:{Port}. Nova tentativa em {Espera}s.",
                    _opcoes.Host, _opcoes.Port, _esperaReconexao.TotalSeconds);
                await FecharAsync();
                await Task.Delay(_esperaReconexao, stoppingToken);
            }
        }
    }

    private async Task ConectarAsync(CancellationToken ct)
    {
        _conexao = await _opcoes.CriarFabrica("tech-challenge-billing-consumidor").CreateConnectionAsync(ct);
        _conexao.ConnectionShutdownAsync += (_, _) => { estado.Marcar(false); return Task.CompletedTask; };
        _conexao.RecoverySucceededAsync += (_, _) => { estado.Marcar(true); return Task.CompletedTask; };

        _canal = await _conexao.CreateChannelAsync(cancellationToken: ct);
        await _canal.BasicQosAsync(0, _opcoes.PrefetchCount, false, ct);

        foreach (var canal in CatalogoCanaisBilling.Consumidos)
        {
            // Declarações idempotentes: o produtor também declara a exchange; a ordem de subida dos serviços não importa.
            await _canal.ExchangeDeclareAsync(canal.Endereco, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);
            await _canal.QueueDeclareAsync(Fila(canal), durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await _canal.QueueDeclareAsync(Dlq(canal), durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await _canal.QueueBindAsync(Fila(canal), canal.Endereco, routingKey: string.Empty, cancellationToken: ct);

            var consumidor = new AsyncEventingBasicConsumer(_canal);
            var contrato = canal;
            consumidor.ReceivedAsync += (_, entrega) => ProcessarAsync(contrato, entrega, ct);
            await _canal.BasicConsumeAsync(Fila(canal), autoAck: false, consumidor, ct);
        }

        estado.Marcar(true);
        logger.LogInformation("Consumindo {Quantidade} canais da Saga no RabbitMQ {Host}:{Port}.",
            CatalogoCanaisBilling.Consumidos.Count, _opcoes.Host, _opcoes.Port);
    }

    private async Task ProcessarAsync(CanalConsumido canal, BasicDeliverEventArgs entrega, CancellationToken ct)
    {
        using var atividade = TelemetriaMensageria.Fonte.StartActivity(
            $"{canal.Endereco} process", ActivityKind.Consumer, ExtrairContextoTrace(entrega.BasicProperties));
        atividade?.SetTag("messaging.system", "rabbitmq");
        atividade?.SetTag("messaging.destination.name", canal.Endereco);
        atividade?.SetTag("messaging.operation.type", "process");

        // O escopo abre antes do processamento: os logs do caso de uso (as transições da Saga, por exemplo) também
        // levam o CorrelationId (CARD-42b). Vem das propriedades da mensagem; o corpo é validado depois, pelo contrato.
        using var escopoLog = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = Guid.TryParse(entrega.BasicProperties.CorrelationId, out var idCorrelacao) ? idCorrelacao : null,
            ["MessageId"] = Guid.TryParse(entrega.BasicProperties.MessageId, out var idMensagem) ? idMensagem : null,
            ["Canal"] = canal.Endereco
        });

        ResultadoProcessamento resultado;
        try
        {
            resultado = await ProcessarComRetentativasAsync(canal, entrega.Body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Desligamento: devolve à fila para outra instância ou para o próximo start.
            await _canal!.BasicNackAsync(entrega.DeliveryTag, multiple: false, requeue: true);
            return;
        }

        atividade?.SetTag("messaging.message.id", resultado.MessageId?.ToString());
        atividade?.SetTag("correlation_id", resultado.CorrelationId?.ToString());

        if (resultado.Status == StatusProcessamento.Rejeitada)
        {
            atividade?.SetStatus(ActivityStatusCode.Error, resultado.Motivo);
            await EnviarParaDlqAsync(canal, entrega, resultado);
            return;
        }

        await _canal!.BasicAckAsync(entrega.DeliveryTag, multiple: false);
    }

    private async Task<ResultadoProcessamento> ProcessarComRetentativasAsync(CanalConsumido canal, ReadOnlyMemory<byte> corpo, CancellationToken ct)
    {
        for (var tentativa = 0; ; tentativa++)
        {
            try
            {
                // Escopo novo por tentativa: o DbContext que falhou (por exemplo, conflito de versão do orçamento) não é reaproveitado
                // e a nova tentativa lê o orçamento atualizado.
                using var escopo = scopeFactory.CreateScope();
                var useCase = escopo.ServiceProvider.GetRequiredService<ProcessarMensagemSagaUseCase>();
                return await useCase.ExecutarAsync(canal.Endereco, corpo, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (tentativa == _esperas.Length)
                {
                    logger.LogError(ex, "Falha transitória persistiu após {Tentativas} tentativas. Canal={Canal}", tentativa + 1, canal.Endereco);
                    return new ResultadoProcessamento(StatusProcessamento.Rejeitada,
                        $"Falha transitória após {tentativa + 1} tentativas: {ex.GetType().Name}.", null, null, null);
                }

                logger.LogWarning(ex, "Falha ao processar mensagem; nova tentativa em {Espera}s. Canal={Canal}",
                    _esperas[tentativa].TotalSeconds, canal.Endereco);
                await Task.Delay(_esperas[tentativa], ct);
            }
        }
    }

    private async Task EnviarParaDlqAsync(CanalConsumido canal, BasicDeliverEventArgs entrega, ResultadoProcessamento resultado)
    {
        var propriedades = new BasicProperties
        {
            Persistent = true,
            ContentType = entrega.BasicProperties.ContentType ?? "application/json",
            Headers = new Dictionary<string, object?>
            {
                ["x-motivo"] = resultado.Motivo,
                ["x-canal-origem"] = canal.Endereco,
                ["x-message-id"] = resultado.MessageId?.ToString(),
                ["x-correlation-id"] = resultado.CorrelationId?.ToString(),
                ["x-rejeitada-em"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        try
        {
            await _canal!.BasicPublishAsync(string.Empty, Dlq(canal), mandatory: false, propriedades, entrega.Body);
            await _canal.BasicAckAsync(entrega.DeliveryTag, multiple: false);
            TelemetriaMensageria.Dlq(canal.Endereco);
            logger.LogWarning("Mensagem enviada para a DLQ {Dlq}. MessageType={MessageType} Motivo={Motivo}",
                Dlq(canal), resultado.MessageType, resultado.Motivo);
        }
        catch (Exception ex)
        {
            // Sem a cópia na DLQ, a mensagem volta para a fila em vez de ser perdida.
            logger.LogError(ex, "Falha ao publicar na DLQ {Dlq}; mensagem devolvida à fila.", Dlq(canal));
            await _canal!.BasicNackAsync(entrega.DeliveryTag, multiple: false, requeue: true);
        }
    }

    internal static ActivityContext ExtrairContextoTrace(IReadOnlyBasicProperties propriedades)
    {
        var cabecalhos = propriedades.Headers;
        if (cabecalhos is null) return default;

        DistributedContextPropagator.Current.ExtractTraceIdAndState(cabecalhos,
            static (carrier, campo, out valor, out valores) =>
            {
                valores = null;
                valor = ((IDictionary<string, object?>)carrier!).TryGetValue(campo, out var bruto)
                    ? bruto switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string texto => texto, _ => null }
                    : null;
            },
            out var traceParent, out var traceState);

        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var contexto) ? contexto : default;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FecharAsync();
    }

    private async Task FecharAsync()
    {
        estado.Marcar(false);
        if (_canal is not null) { await _canal.DisposeAsync(); _canal = null; }
        if (_conexao is not null) { await _conexao.DisposeAsync(); _conexao = null; }
    }
}
