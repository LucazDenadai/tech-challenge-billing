using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

// Duas réplicas despachando a mesma outbox ao mesmo tempo (CARD-41b): com FOR UPDATE SKIP LOCKED, cada evento
// é publicado uma vez e todos ficam marcados.
[Collection(PostgresCollection.Nome)]
public class DespachanteOutboxTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Canal = "saga-os.payment-pending.v1";
    private const string Fila = "teste.despachante";

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-alpine")
        .WithUsername("billing-teste").WithPassword("billing-teste-senha").Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    [Fact]
    public async Task DuasReplicasAoMesmoTempo_CadaEventoPublicadoUmaVez()
    {
        var banco = postgres.NovoBancoVazio();
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            await db.Database.MigrateAsync();
            for (var i = 0; i < 30; i++)
                db.Outbox.Add(new MensagemOutbox
                {
                    MessageId = Guid.NewGuid(), MessageType = "PaymentPending.v1", Canal = Canal, CorrelationId = Guid.NewGuid(),
                    Payload = "{}", CriadaEmUtc = DateTimeOffset.UtcNow.AddMilliseconds(i)
                });
            await db.SaveChangesAsync();
        }

        var uri = new Uri(_rabbit.GetConnectionString());
        var opcoes = Options.Create(new RabbitMqOptions { Enabled = true, Host = uri.Host, Port = uri.Port, Username = "billing-teste", Password = "billing-teste-senha" });
        await using var conexao = await opcoes.Value.CriarFabrica("teste").CreateConnectionAsync();
        await using var canal = await conexao.CreateChannelAsync();
        await canal.ExchangeDeclareAsync(Canal, ExchangeType.Fanout, durable: true, autoDelete: false);
        await canal.QueueDeclareAsync(Fila, durable: false, exclusive: false, autoDelete: false);
        await canal.QueueBindAsync(Fila, Canal, string.Empty);

        var servicos = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(banco)).BuildServiceProvider();
        DespachanteOutboxHostedService Replica() => new(servicos.GetRequiredService<IServiceScopeFactory>(),
            new PublicadorRabbitMq(opcoes), opcoes, NullLogger<DespachanteOutboxHostedService>.Instance);
        var (a, b) = (Replica(), Replica());

        // Cada réplica despacha até não sobrar nada; as duas rodam em paralelo sobre o mesmo banco.
        async Task<int> Esgotar(DespachanteOutboxHostedService despachante)
        {
            var total = 0;
            int lidas;
            while ((lidas = await despachante.DespacharAsync(CancellationToken.None)) > 0)
                total += lidas;
            return total;
        }
        var totais = await Task.WhenAll(Esgotar(a), Esgotar(b));

        Assert.Equal(30, totais.Sum());
        var publicadas = new List<string>();
        BasicGetResult? resultado;
        while ((resultado = await canal.BasicGetAsync(Fila, autoAck: true)) is not null)
            publicadas.Add(resultado.BasicProperties.MessageId!);
        Assert.Equal(30, publicadas.Count);
        Assert.Equal(30, publicadas.Distinct().Count());
        await using var leitura = PostgresFixture.CriarContexto(banco);
        Assert.Equal(0, await leitura.Outbox.CountAsync(m => m.PublicadaEmUtc == null));
    }
}
