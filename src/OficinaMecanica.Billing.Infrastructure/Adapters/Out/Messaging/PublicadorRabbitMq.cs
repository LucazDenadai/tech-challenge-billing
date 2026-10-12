using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using RabbitMQ.Client;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Messaging;

public sealed record MensagemParaPublicar(Guid MessageId, string MessageType, string Canal, Guid CorrelationId, string Payload, string? TraceParent);

// Publica na exchange fanout do canal (ADR-018) com confirmação do broker; uma publicação por vez no mesmo canal AMQP.
public sealed class PublicadorRabbitMq(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly RabbitMqOptions _opcoes = options.Value;
    private readonly SemaphoreSlim _trava = new(1, 1);
    private readonly HashSet<string> _exchangesDeclaradas = [];
    private IConnection? _conexao;
    private IChannel? _canal;

    public async Task PublicarAsync(MensagemParaPublicar mensagem, CancellationToken ct)
    {
        await _trava.WaitAsync(ct);
        try
        {
            var canal = await ObterCanalAsync(ct);
            if (_exchangesDeclaradas.Add(mensagem.Canal))
                await canal.ExchangeDeclareAsync(mensagem.Canal, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

            // Span de publicação filho do trace que gerou o evento; o traceparent vai no cabeçalho para o consumidor continuar.
            ActivityContext.TryParse(mensagem.TraceParent, null, isRemote: false, out var pai);
            using var atividade = TelemetriaMensageria.Fonte.StartActivity($"{mensagem.Canal} publish", ActivityKind.Producer, pai);
            atividade?.SetTag("messaging.system", "rabbitmq");
            atividade?.SetTag("messaging.destination.name", mensagem.Canal);
            atividade?.SetTag("messaging.operation.type", "publish");
            atividade?.SetTag("messaging.message.id", mensagem.MessageId.ToString());
            atividade?.SetTag("correlation_id", mensagem.CorrelationId.ToString());

            var cabecalhos = new Dictionary<string, object?>();
            if ((atividade?.Id ?? mensagem.TraceParent) is { } traceParent)
                cabecalhos["traceparent"] = traceParent;

            var propriedades = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = mensagem.MessageId.ToString(),
                CorrelationId = mensagem.CorrelationId.ToString(),
                Type = mensagem.MessageType,
                Headers = cabecalhos
            };

            // Com confirmação do broker, o await só termina depois que o RabbitMQ aceitou a mensagem.
            await canal.BasicPublishAsync(mensagem.Canal, string.Empty, mandatory: false, propriedades,
                Encoding.UTF8.GetBytes(mensagem.Payload), ct);
        }
        catch
        {
            await FecharAsync();
            throw;
        }
        finally
        {
            _trava.Release();
        }
    }

    private async Task<IChannel> ObterCanalAsync(CancellationToken ct)
    {
        if (_canal is { IsOpen: true })
            return _canal;

        await FecharAsync();
        _conexao = await _opcoes.CriarFabrica("tech-challenge-billing-outbox").CreateConnectionAsync(ct);
        _canal = await _conexao.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        return _canal;
    }

    private async Task FecharAsync()
    {
        _exchangesDeclaradas.Clear();
        if (_canal is not null) { await _canal.DisposeAsync(); _canal = null; }
        if (_conexao is not null) { await _conexao.DisposeAsync(); _conexao = null; }
    }

    public async ValueTask DisposeAsync() => await FecharAsync();
}
