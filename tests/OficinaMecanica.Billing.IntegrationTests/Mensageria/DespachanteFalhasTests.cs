using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Mensageria;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Billing.IntegrationTests.Mensageria;

// Despachante da outbox diante de recusa do broker, de lote cheio e de banco fora do ar.
[Collection(PostgresCollection.Nome)]
public class DespachanteFalhasTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string CanalComOutroTipo = "saga-os.canal-com-outro-tipo.v1";
    private const string Canal = "saga-os.quote-ready.v1";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("billing-teste").WithPassword("billing-teste-senha").Build();

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();
        // O publicador declara a exchange como fanout; com outro tipo já declarado, o broker recusa (PRECONDITION_FAILED).
        await using var conexao = await Opcoes().Value.CriarFabrica("teste").CreateConnectionAsync();
        await using var canal = await conexao.CreateChannelAsync();
        await canal.ExchangeDeclareAsync(CanalComOutroTipo, ExchangeType.Direct, durable: true, autoDelete: false);
    }

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    private IOptions<RabbitMqOptions> Opcoes(int intervaloMs = 500)
    {
        var uri = new Uri(_rabbit.GetConnectionString());
        return Options.Create(new RabbitMqOptions
        {
            Enabled = true, Host = uri.Host, Port = uri.Port, Username = "billing-teste", Password = "billing-teste-senha", IntervaloOutboxMs = intervaloMs
        });
    }

    private static DespachanteOutboxHostedService Despachante(string banco, IOptions<RabbitMqOptions> opcoes, ILogger<DespachanteOutboxHostedService>? logger = null)
    {
        var servicos = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(banco)).BuildServiceProvider();
        return new DespachanteOutboxHostedService(servicos.GetRequiredService<IServiceScopeFactory>(), new PublicadorRabbitMq(opcoes), opcoes,
            logger ?? NullLogger<DespachanteOutboxHostedService>.Instance);
    }

    private static MensagemOutbox Mensagem(string canal, int ordem = 0) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = "QuoteReady.v1", Canal = canal, CorrelationId = Guid.NewGuid(),
        Payload = "{}", CriadaEmUtc = DateTimeOffset.UtcNow.AddMilliseconds(ordem)
    };

    [Fact]
    public async Task BrokerRecusa_RegistraTentativaEMantemPendente()
    {
        var banco = await postgres.NovoBancoMigradoAsync();
        var mensagem = Mensagem(CanalComOutroTipo);
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            db.Outbox.Add(mensagem);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => Despachante(banco, Opcoes()).DespacharAsync(CancellationToken.None));

        await using var leitura = PostgresFixture.CriarContexto(banco);
        var gravada = await leitura.Outbox.SingleAsync(m => m.MessageId == mensagem.MessageId);
        Assert.Null(gravada.PublicadaEmUtc);
        Assert.Equal(1, gravada.Tentativas);
        Assert.False(string.IsNullOrEmpty(gravada.UltimoErro));
    }

    [Fact]
    public async Task LoteCheio_SegueSemEsperarEDepoisFicaOcioso()
    {
        var banco = await postgres.NovoBancoMigradoAsync();
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            // 50 é o lote do despachante: o primeiro ciclo lê o lote cheio e segue direto para o próximo.
            db.Outbox.AddRange(Enumerable.Range(0, 51).Select(i => Mensagem(Canal, i)));
            await db.SaveChangesAsync();
        }
        var despachante = Despachante(banco, Opcoes(intervaloMs: 50));

        await despachante.StartAsync(CancellationToken.None);
        await Aguardar(async () =>
        {
            await using var db = PostgresFixture.CriarContexto(banco);
            return await db.Outbox.AllAsync(m => m.PublicadaEmUtc != null);
        });
        await Task.Delay(200);
        await despachante.StopAsync(CancellationToken.None);

        Assert.False(despachante.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task BancoForaDoAr_RegistraEAguardaParaTentarDeNovo()
    {
        var logs = new ConcurrentQueue<string>();
        var despachante = Despachante("Host=localhost;Port=1;Database=inexistente;Username=x;Password=x;Timeout=1", Opcoes(),
            new LoggerQueGuarda(logs));

        await despachante.StartAsync(CancellationToken.None);
        await Aguardar(() => Task.FromResult(logs.Count(m => m.Contains("Falha ao despachar a outbox")) >= 2), TimeSpan.FromSeconds(20));
        await despachante.StopAsync(CancellationToken.None);
    }

    private static async Task Aguardar(Func<Task<bool>> condicao, TimeSpan? limite = null)
    {
        var fim = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < fim)
        {
            if (await condicao()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condição não atingida.");
    }

    private sealed class LoggerQueGuarda(ConcurrentQueue<string> mensagens) : ILogger<DespachanteOutboxHostedService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => mensagens.Enqueue(formatter(state, exception));
    }
}
