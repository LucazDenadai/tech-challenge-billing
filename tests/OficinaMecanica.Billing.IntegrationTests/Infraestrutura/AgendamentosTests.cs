using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Agendamento;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;

namespace OficinaMecanica.Billing.IntegrationTests.Infraestrutura;

// Laços da expiração de orçamentos e da reconciliação de pagamentos: lote cheio segue sem esperar, o resto
// espera o intervalo, e falha de banco vira aviso com nova tentativa no próximo intervalo.
[Collection(PostgresCollection.Nome)]
public class AgendamentosTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _ontem = DateTimeOffset.UtcNow.AddDays(-2);
    private readonly ConcurrentQueue<string> _logs = new();

    private ServiceProvider Servicos(string banco) => new ServiceCollection()
        .AddLogging(l => l.AddProvider(new LogsCapturados(_logs)))
        .AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = banco,
            ["Orcamento:IntervaloExpiracaoSegundos"] = "1",
            ["MercadoPago:IntervaloReconciliacaoSegundos"] = "1"
        }).Build())
        .BuildServiceProvider();

    private static T Agendamento<T>(ServiceProvider servicos) where T : IHostedService
        => servicos.GetServices<IHostedService>().OfType<T>().Single();

    [Fact]
    public async Task Expiracao_LoteCheioSegueEOResto()
    {
        var banco = await postgres.NovoBancoMigradoAsync();
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            db.Orcamentos.AddRange(Enumerable.Range(0, 51).Select(i => Orcamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                $"q-{i}", "BRL", _ontem, [new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Peca, "Filtro", 1, 10m)], _ontem, TimeSpan.FromHours(24))));
            await db.SaveChangesAsync();
        }
        await using var servicos = Servicos(banco);
        var expiracao = Agendamento<ExpiracaoOrcamentosHostedService>(servicos);

        await expiracao.StartAsync(CancellationToken.None);
        await Aguardar(async () =>
        {
            await using var db = PostgresFixture.CriarContexto(banco);
            return await db.Outbox.CountAsync(m => m.MessageType == "QuoteExpired.v1") == 51;
        });
        await expiracao.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconciliacao_LoteCheioSegueEOResto()
    {
        var banco = await postgres.NovoBancoMigradoAsync();
        await using (var db = PostgresFixture.CriarContexto(banco))
        {
            // Cobranças sem resposta do provedor, com verificação vencida: a reconciliação refaz a criação.
            db.Pagamentos.AddRange(Enumerable.Range(0, 21).Select(i => Pagamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), 1, 10m, "BRL", $"p-{i}", Guid.NewGuid(), "cliente@exemplo.com", _ontem, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2))));
            await db.SaveChangesAsync();
        }
        await using var servicos = Servicos(banco);
        var reconciliacao = Agendamento<ReconciliacaoPagamentosHostedService>(servicos);

        await reconciliacao.StartAsync(CancellationToken.None);
        await Aguardar(async () =>
        {
            await using var db = PostgresFixture.CriarContexto(banco);
            return await db.Pagamentos.CountAsync(p => p.Status == StatusPagamento.Pendente) == 21;
        });
        await reconciliacao.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BancoForaDoAr_OsDoisAgendamentosAvisamEContinuam()
    {
        await using var servicos = Servicos("Host=localhost;Port=1;Database=inexistente;Username=x;Password=x;Timeout=1");
        var expiracao = Agendamento<ExpiracaoOrcamentosHostedService>(servicos);
        var reconciliacao = Agendamento<ReconciliacaoPagamentosHostedService>(servicos);

        await expiracao.StartAsync(CancellationToken.None);
        await reconciliacao.StartAsync(CancellationToken.None);
        await Aguardar(() => Task.FromResult(
            _logs.Count(m => m.Contains("Falha ao expirar orçamentos")) >= 2 && _logs.Count(m => m.Contains("Falha na reconciliação")) >= 2));
        await expiracao.StopAsync(CancellationToken.None);
        await reconciliacao.StopAsync(CancellationToken.None);
    }

    private static async Task Aguardar(Func<Task<bool>> condicao)
    {
        var fim = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < fim)
        {
            if (await condicao()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condição não atingida em 30 s.");
    }

    private sealed class LogsCapturados(ConcurrentQueue<string> mensagens) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoria) => new Registrador(mensagens);

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
