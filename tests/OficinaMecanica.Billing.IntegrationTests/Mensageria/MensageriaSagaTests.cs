using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace OficinaMecanica.Billing.IntegrationTests.Mensageria;

// Fluxo real pelo RabbitMQ e PostgreSQL: QuoteRequested -> inbox + orçamento + outbox -> evento publicado (CARD-39a).
[Collection(PostgresCollection.Nome)]
public class MensageriaSagaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Usuario = "billing-teste";
    private const string Senha = "billing-teste-senha";
    private const string Pedido = "saga-os.quote-requested.v1";
    private const string Pronto = "saga-os.quote-ready.v1";
    private const string Recusado = "saga-os.quote-rejected.v1";
    private const string Aprovado = "saga-os.quote-approved.v1";
    private const string Expirado = "saga-os.quote-expired.v1";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername(Usuario)
        .WithPassword(Senha)
        .Build();

    // Sem OpenTelemetry no host de teste: o listener faz a fonte de mensageria criar spans de verdade.
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = fonte => fonte.Name == TelemetriaMensageria.NomeFonte,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    private readonly RelogioFixo _relogio = new(DateTimeOffset.UtcNow);
    private IHost _host = null!;
    private string _conexaoBanco = null!;
    private IConnection _conexao = null!;
    private IChannel _canal = null!;

    public async Task InitializeAsync()
    {
        ActivitySource.AddActivityListener(_listener);
        await _rabbit.StartAsync();
        var uri = new Uri(_rabbit.GetConnectionString());

        _conexaoBanco = await postgres.NovoBancoMigradoAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _conexaoBanco,
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:Host"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:Username"] = Usuario,
            ["RabbitMq:Password"] = Senha,
            ["RabbitMq:IntervaloOutboxMs"] = "100",
            ["Orcamento:IntervaloExpiracaoSegundos"] = "1"
        });
        builder.Services.AddInfrastructure(builder.Configuration).AddMensageria(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(_relogio);
        _host = builder.Build();
        await _host.StartAsync();

        var estado = _host.Services.GetRequiredService<EstadoConsumidorSaga>();
        await Aguardar(() => Task.FromResult(estado.Consumindo));

        _conexao = await new ConnectionFactory { Uri = uri, UserName = Usuario, Password = Senha }.CreateConnectionAsync();
        _canal = await _conexao.CreateChannelAsync();
        foreach (var evento in new[] { Pronto, Recusado, Aprovado, Expirado })
        {
            await _canal.ExchangeDeclareAsync(evento, ExchangeType.Fanout, durable: true, autoDelete: false);
            await _canal.QueueDeclareAsync(FilaTeste(evento), durable: false, exclusive: false, autoDelete: false);
            await _canal.QueueBindAsync(FilaTeste(evento), evento, string.Empty);
        }
    }

    public async Task DisposeAsync()
    {
        await _canal.DisposeAsync();
        await _conexao.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        await _rabbit.DisposeAsync();
        _listener.Dispose();
    }

    [Fact]
    public async Task QuoteRequested_PublicaQuoteReadyCorrelacionadoNoMesmoTrace()
    {
        var pedido = Montagem.Pedido();
        var traceId = ActivityTraceId.CreateRandom();

        await PublicarAsync(Pedido, pedido, $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        var (evento, propriedades) = await ReceberAsync(FilaTeste(Pronto));
        Assert.Equal("QuoteReady.v1", evento["messageType"]!.GetValue<string>());
        Assert.Equal(pedido["messageId"]!.GetValue<string>(), evento["causationId"]!.GetValue<string>());
        Assert.Equal(pedido["correlationId"]!.GetValue<string>(), evento["correlationId"]!.GetValue<string>());
        Assert.Equal(125.90m, evento["amount"]!.GetValue<decimal>());
        Assert.Equal(1, evento["quoteVersion"]!.GetValue<int>());
        Assert.Equal(evento["messageId"]!.GetValue<string>(), propriedades.MessageId);

        var traceParent = Encoding.UTF8.GetString((byte[])propriedades.Headers!["traceparent"]!);
        Assert.Contains(traceId.ToString(), traceParent);

        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(1, await db.Orcamentos.CountAsync());
        Assert.NotNull((await db.Outbox.SingleAsync()).PublicadaEmUtc);
    }

    [Fact]
    public async Task MesmaMensagemDuasVezes_UmOrcamentoEUmEvento()
    {
        var pedido = Montagem.Pedido();

        await PublicarAsync(Pedido, pedido);
        await PublicarAsync(Pedido, pedido);

        await ReceberAsync(FilaTeste(Pronto));
        await Aguardar(async () => await _canal.MessageCountAsync($"billing.{Pedido}") == 0);
        await Task.Delay(500);
        Assert.Equal(0u, await _canal.MessageCountAsync(FilaTeste(Pronto)));

        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(1, await db.Inbox.CountAsync());
    }

    [Fact]
    public async Task ItemSemPreco_PublicaQuoteRejected()
    {
        var pedido = Montagem.Pedido();
        ((JsonObject)pedido["items"]![0]!).Remove("unitPrice");

        await PublicarAsync(Pedido, pedido);

        var (evento, _) = await ReceberAsync(FilaTeste(Recusado));
        Assert.Contains("sem preço", evento["reason"]!.GetValue<string>());
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(0, await db.Orcamentos.CountAsync());
    }

    [Fact]
    public async Task PedidoForaDoContrato_VaiParaDlqComMotivo()
    {
        var pedido = Montagem.Pedido();
        pedido["clienteId"] = "nao-e-uuid";

        await PublicarAsync(Pedido, pedido);

        var (_, propriedades) = await ReceberAsync($"billing.{Pedido}.dlq");
        Assert.Contains("contrato", Encoding.UTF8.GetString((byte[])propriedades.Headers!["x-motivo"]!));
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(0, await db.Inbox.CountAsync());
    }

    [Fact]
    public async Task AprovacaoDoCliente_PublicaQuoteApproved()
    {
        var clienteId = Guid.NewGuid();
        await PublicarAsync(Pedido, Montagem.Pedido(clienteId: clienteId));
        var (pronto, _) = await ReceberAsync(FilaTeste(Pronto));
        var quoteId = Guid.Parse(pronto["quoteId"]!.GetValue<string>());

        using (var escopo = _host.Services.CreateScope())
        {
            var decidir = escopo.ServiceProvider.GetRequiredService<DecidirOrcamentoUseCase>();
            await decidir.ExecutarAsync(new DecidirOrcamentoCommand(quoteId, 1, ResultadoDecisao.Aprovado, clienteId, null));
        }

        var (aprovado, _) = await ReceberAsync(FilaTeste(Aprovado));
        Assert.Equal(quoteId.ToString(), aprovado["quoteId"]!.GetValue<string>());
        Assert.Equal(pronto["correlationId"]!.GetValue<string>(), aprovado["correlationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task OrcamentoSemDecisaoNoPrazo_ServicoDeExpiracaoPublicaQuoteExpired()
    {
        await PublicarAsync(Pedido, Montagem.Pedido());
        var (pronto, _) = await ReceberAsync(FilaTeste(Pronto));

        _relogio.Agora += TimeSpan.FromHours(24);

        var (expirado, _) = await ReceberAsync(FilaTeste(Expirado));
        Assert.Equal(pronto["quoteId"]!.GetValue<string>(), expirado["quoteId"]!.GetValue<string>());
        await using var db = PostgresFixture.CriarContexto(_conexaoBanco);
        Assert.Equal(StatusVersaoOrcamento.Expirada, (await db.Set<VersaoOrcamento>().SingleAsync()).Status);
    }

    private static string FilaTeste(string exchange) => $"teste.{exchange}";

    private async Task PublicarAsync(string exchange, JsonObject mensagem, string? traceParent = null)
    {
        var propriedades = new BasicProperties { ContentType = "application/json", Persistent = true };
        if (traceParent is not null)
            propriedades.Headers = new Dictionary<string, object?> { ["traceparent"] = traceParent };

        await _canal.BasicPublishAsync(exchange, string.Empty, mandatory: false, propriedades, Encoding.UTF8.GetBytes(mensagem.ToJsonString()));
    }

    private async Task<(JsonObject Corpo, IReadOnlyBasicProperties Propriedades)> ReceberAsync(string fila)
    {
        BasicGetResult? resultado = null;
        await Aguardar(async () => (resultado = await _canal.BasicGetAsync(fila, autoAck: true)) is not null);
        return (JsonNode.Parse(resultado!.Body.Span)!.AsObject(), resultado.BasicProperties);
    }

    private static async Task Aguardar(Func<Task<bool>> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await condicao()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condição não atingida em 30 s.");
    }
}
