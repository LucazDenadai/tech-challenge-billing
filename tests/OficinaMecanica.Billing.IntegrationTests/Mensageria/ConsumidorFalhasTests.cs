using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Billing.IntegrationTests.Mensageria;

// Caminhos de falha do consumidor (ADR-017): falha transitória até a DLQ, corpo ilegível, desligamento no meio do
// processamento e broker fora do ar. A transação é trocada por uma que falha ou trava de propósito.
[Collection(PostgresCollection.Nome)]
public class ConsumidorFalhasTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Usuario = "billing-teste";
    private const string Senha = "billing-teste-senha";
    private const string PedidoReserva = "saga-os.quote-requested.v1";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine").WithUsername(Usuario).WithPassword(Senha).Build();
    private readonly ControleTransacao _controle = new();
    private readonly LogsCapturados _logs = new();

    private string _conexaoBanco = null!;

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();
        _conexaoBanco = await postgres.NovoBancoMigradoAsync();
    }

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    [Fact]
    public async Task FalhaTransitoriaQuePersiste_TresNovasTentativasEDepoisDlq()
    {
        _controle.Falhas = 4;
        using var host = await SubirAsync();
        await using var conexao = await ConectarAsync();
        await using var canal = await conexao.CreateChannelAsync();

        // Como o OS publica: MessageId e CorrelationId também nas propriedades AMQP.
        var pedido = Pedido();
        await canal.BasicPublishAsync(PedidoReserva, string.Empty, mandatory: false, new BasicProperties
        {
            ContentType = "application/json",
            MessageId = pedido["messageId"]!.GetValue<string>(),
            CorrelationId = pedido["correlationId"]!.GetValue<string>()
        }, Encoding.UTF8.GetBytes(pedido.ToJsonString()));

        // Esperas de 1 s, 5 s e 10 s entre as tentativas.
        var dlq = await ReceberAsync(canal, $"billing.{PedidoReserva}.dlq", TimeSpan.FromSeconds(40));
        Assert.Contains("após 4 tentativas", Encoding.UTF8.GetString((byte[])dlq.BasicProperties.Headers!["x-motivo"]!));
        // Os cabeçalhos levam a referência da mensagem, lida do corpo (CARD-44).
        Assert.Equal(pedido["correlationId"]!.GetValue<string>(), Encoding.UTF8.GetString((byte[])dlq.BasicProperties.Headers!["x-correlation-id"]!));
        Assert.Equal(pedido["messageId"]!.GetValue<string>(), Encoding.UTF8.GetString((byte[])dlq.BasicProperties.Headers!["x-message-id"]!));
        Assert.Equal(4, _controle.Chamadas);
        await host.StopAsync();
    }

    [Fact]
    public async Task CorpoIlegivelSemPropriedades_VaiParaDlqSemIdentificadores()
    {
        // Com trace ativo: o span do processamento fica sem messageId e correlation_id e marcado como erro.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = fonte => fonte.Name == TelemetriaMensageria.NomeFonte,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var host = await SubirAsync();
        await using var conexao = await ConectarAsync();
        await using var canal = await conexao.CreateChannelAsync();

        await canal.BasicPublishAsync(PedidoReserva, string.Empty, Encoding.UTF8.GetBytes("não é json"));

        var dlq = await ReceberAsync(canal, $"billing.{PedidoReserva}.dlq", TimeSpan.FromSeconds(30));
        var cabecalhos = dlq.BasicProperties.Headers!;
        Assert.Equal("application/json", dlq.BasicProperties.ContentType);
        Assert.Null(cabecalhos["x-message-id"]);
        Assert.Null(cabecalhos["x-correlation-id"]);
        Assert.Equal(0, _controle.Chamadas);
        var span = Assert.Single(spans, a => a.DisplayName == $"{PedidoReserva} process");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.GetTagItem("correlation_id"));
        await host.StopAsync();
    }

    [Fact]
    public async Task DesligamentoNoMeioDoProcessamento_DevolveAMensagemAFila()
    {
        _controle.Travar = true;
        using var host = await SubirAsync();
        await using var conexao = await ConectarAsync();
        await using var canal = await conexao.CreateChannelAsync();

        await canal.BasicPublishAsync(PedidoReserva, string.Empty, mandatory: false, new BasicProperties { ContentType = "application/json" },
            Encoding.UTF8.GetBytes(Pedido().ToJsonString()));
        await _controle.Entrou.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await host.StopAsync();

        var devolvida = await ReceberAsync(canal, $"billing.{PedidoReserva}", TimeSpan.FromSeconds(30));
        Assert.True(devolvida.Redelivered);
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(0, await db.Inbox.CountAsync());
    }

    [Fact]
    public async Task BrokerForaDoAr_RegistraEAguardaParaReconectar()
    {
        // Porta sem broker: a conexão é recusada na hora.
        using var host = await SubirAsync(porta: 1, aguardarConsumo: false);

        await Aguardar(() => _logs.Mensagens.Any(m => m.Contains("Falha ao conectar ao RabbitMQ")), TimeSpan.FromSeconds(30));

        Assert.False(host.Services.GetRequiredService<EstadoConsumidorSaga>().Consumindo);
        await host.StopAsync();
    }

    [Fact]
    public void ContextoDoTrace_LidoDeTextoOuBytesEIgnoradoQuandoInvalido()
    {
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        static ActivityContext Ler(object? valor) => ConsumidorSagaHostedService.ExtrairContextoTrace(
            new BasicProperties { Headers = new Dictionary<string, object?> { ["traceparent"] = valor } });

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", Ler(traceParent).TraceId.ToHexString());
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", Ler(Encoding.UTF8.GetBytes(traceParent)).TraceId.ToHexString());
        Assert.Equal(default, Ler(42));
        Assert.Equal(default, Ler("não é traceparent"));
        Assert.Equal(default, ConsumidorSagaHostedService.ExtrairContextoTrace(new BasicProperties()));
    }

    private async Task<IHost> SubirAsync(int? porta = null, bool aguardarConsumo = true)
    {
        var uri = new Uri(_rabbit.GetConnectionString());
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _conexaoBanco,
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:Host"] = uri.Host,
            ["RabbitMq:Port"] = (porta ?? uri.Port).ToString(),
            ["RabbitMq:Username"] = Usuario,
            ["RabbitMq:Password"] = Senha,
            ["Orcamento:ExpiracaoHabilitada"] = "false",
            ["MercadoPago:ReconciliacaoHabilitada"] = "false"
        });
        builder.Logging.AddProvider(_logs);
        builder.Services.AddInfrastructure(builder.Configuration).AddMensageria(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Scoped<ITransacao>(sp =>
            new TransacaoInstavel(new Transacao(sp.GetRequiredService<AppDbContext>()), _controle)));

        var host = builder.Build();
        await host.StartAsync();
        if (aguardarConsumo)
        {
            var estado = host.Services.GetRequiredService<EstadoConsumidorSaga>();
            await Aguardar(() => estado.Consumindo, TimeSpan.FromSeconds(30));
        }
        return host;
    }

    private Task<IConnection> ConectarAsync()
        => new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()), UserName = Usuario, Password = Senha }.CreateConnectionAsync();

    private static JsonObject Pedido() => Montagem.Pedido();

    private static async Task<BasicGetResult> ReceberAsync(IChannel canal, string fila, TimeSpan limite)
    {
        BasicGetResult? resultado = null;
        await Aguardar(() => (resultado = canal.BasicGetAsync(fila, autoAck: true).GetAwaiter().GetResult()) is not null, limite);
        return resultado!;
    }

    private static async Task Aguardar(Func<bool> condicao, TimeSpan limite)
    {
        var fim = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fim)
        {
            if (condicao()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Condição não atingida em {limite.TotalSeconds} s.");
    }

    private sealed class ControleTransacao
    {
        public int Falhas;
        public bool Travar;
        public int Chamadas;
        public readonly TaskCompletionSource Entrou = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Falha as primeiras chamadas como um banco instável, ou trava até o desligamento cancelar o token.
    private sealed class TransacaoInstavel(ITransacao real, ControleTransacao controle) : ITransacao
    {
        public async Task<T> ExecutarAsync<T>(Func<Task<T>> acao, CancellationToken ct = default)
        {
            var chamada = Interlocked.Increment(ref controle.Chamadas);
            if (controle.Travar)
            {
                controle.Entrou.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (chamada <= controle.Falhas)
                throw new TimeoutException("Banco instável.");
            return await real.ExecutarAsync(acao, ct);
        }
    }

    private sealed class LogsCapturados : ILoggerProvider
    {
        public ConcurrentQueue<string> Mensagens { get; } = new();

        public ILogger CreateLogger(string categoria) => new Registrador(Mensagens);

        public void Dispose() { }

        private sealed class Registrador(ConcurrentQueue<string> mensagens) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => mensagens.Enqueue(formatter(state, exception));
        }
    }
}
