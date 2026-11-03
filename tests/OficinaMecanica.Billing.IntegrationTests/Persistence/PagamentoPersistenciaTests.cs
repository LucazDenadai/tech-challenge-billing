using OficinaMecanica.Billing.Application.Ports.Out;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;

namespace OficinaMecanica.Billing.IntegrationTests.Persistence;

// Pagamento no PostgreSQL de Billing (CARD-39b): atomicidade com inbox/outbox, unicidade e concorrência pelo xmin.
[Collection(PostgresCollection.Nome)]
public class PagamentoPersistenciaTests(PostgresFixture fixture)
{
    private sealed record OrcamentoAprovado(string Conexao, Guid OsId, Guid FilialId, Guid QuoteId);

    private async Task<OrcamentoAprovado> OrcamentoAprovadoAsync()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var clienteId = Guid.NewGuid();
        var pedido = Montagem.Pedido(clienteId: clienteId);

        await using (var db = PostgresFixture.CriarContexto(conexao))
            await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(pedido));

        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var orcamento = await db.Orcamentos.SingleAsync();
            await Montagem.Decidir(db).ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, clienteId, null));
            return new OrcamentoAprovado(conexao, orcamento.OsId, orcamento.FilialId, orcamento.Id);
        }
    }

    private static async Task<StatusProcessamento> CobrarAsync(OrcamentoAprovado o, ProvedorDeTeste provedor, Action<System.Text.Json.Nodes.JsonObject>? ajustar = null)
    {
        var pedido = Montagem.PedidoCobranca(o.OsId, o.FilialId, o.QuoteId, 125.90m);
        ajustar?.Invoke(pedido);
        await using var db = PostgresFixture.CriarContexto(o.Conexao);
        return (await Montagem.Processar(db, provedor: provedor).ExecutarAsync(Montagem.CanalCobranca, Montagem.Bytes(pedido))).Status;
    }

    [Fact]
    public async Task Cobranca_GravaPagamentoInboxEOutboxEApagaOEmailDepoisDeCriar()
    {
        var o = await OrcamentoAprovadoAsync();
        var provedor = new ProvedorDeTeste();

        Assert.Equal(StatusProcessamento.Processada, await CobrarAsync(o, provedor));

        await using var leitura = PostgresFixture.CriarContexto(o.Conexao);
        var pagamento = await leitura.Pagamentos.SingleAsync();
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Equal(125.90m, pagamento.Valor);
        Assert.Null(pagamento.EmailPagador);
        Assert.Equal("00020126pix-teste", pagamento.CodigoPix);
        Assert.Contains("PaymentPending.v1", await leitura.Outbox.Select(m => m.MessageType).ToListAsync());
        Assert.Equal(1, provedor.Cobrancas);
    }

    [Fact]
    public async Task CobrancaParaOrcamentoNaoAprovado_NaoGravaNada()
    {
        var o = await OrcamentoAprovadoAsync();

        var status = await CobrarAsync(o, new ProvedorDeTeste(), p => p["quoteId"] = Guid.NewGuid().ToString());

        Assert.Equal(StatusProcessamento.Rejeitada, status);
        await using var leitura = PostgresFixture.CriarContexto(o.Conexao);
        Assert.Equal(0, await leitura.Pagamentos.CountAsync());
    }

    [Fact]
    public async Task DoisComandosSimultaneosComAMesmaChave_UmPagamentoSo()
    {
        var o = await OrcamentoAprovadoAsync();
        var provedor = new ProvedorDeTeste();

        var tentativas = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { return (StatusProcessamento?)await CobrarAsync(o, provedor); }
            catch (Exception) { return null; }
        }));

        // Quem perdeu a corrida recebe conflito (o consumidor refaz a mensagem). Quem chegou depois do commit pode
        // chamar o provedor de novo, mas com a mesma chave idempotente: continua uma cobrança só.
        Assert.Contains(StatusProcessamento.Processada, tentativas);
        await using var leitura = PostgresFixture.CriarContexto(o.Conexao);
        Assert.Equal(1, await leitura.Pagamentos.CountAsync());
        Assert.Equal(1, provedor.Cobrancas);
    }

    [Fact]
    public async Task DuasReconciliacoesSimultaneas_UmEventoDeAprovacao()
    {
        var o = await OrcamentoAprovadoAsync();
        var provedor = new ProvedorDeTeste();
        await CobrarAsync(o, provedor);
        string referencia;
        await using (var db = PostgresFixture.CriarContexto(o.Conexao))
            referencia = (await db.Pagamentos.SingleAsync()).ReferenciaProvedor!;
        provedor.Aprovar(referencia);

        // Dois contextos leem o mesmo Pendente antes de qualquer um gravar.
        await using var a = PostgresFixture.CriarContexto(o.Conexao);
        await using var b = PostgresFixture.CriarContexto(o.Conexao);
        var pagamentoA = await new PagamentoRepository(a).ObterPorIdempotencyKeyAsync($"pagamento:{o.OsId}");
        var pagamentoB = await new PagamentoRepository(b).ObterPorIdempotencyKeyAsync($"pagamento:{o.OsId}");

        await Montagem.Reconciliar(a, provedor).ConsultarAsync(pagamentoA!.Id);
        await Montagem.Reconciliar(b, provedor).ConsultarAsync(pagamentoB!.Id);

        await using var leitura = PostgresFixture.CriarContexto(o.Conexao);
        Assert.Equal(StatusPagamento.Aprovado, (await leitura.Pagamentos.SingleAsync()).Status);
        Assert.Equal(1, await leitura.Outbox.CountAsync(m => m.MessageType == "PaymentApproved.v1"));
    }

    [Fact]
    public async Task MesmaNotificacaoEmDuasReplicas_GravadaEProcessadaUmaVez()
    {
        var o = await OrcamentoAprovadoAsync();
        var provedor = new ProvedorDeTeste();
        await CobrarAsync(o, provedor);
        string referencia;
        await using (var db = PostgresFixture.CriarContexto(o.Conexao))
            referencia = (await db.Pagamentos.SingleAsync()).ReferenciaProvedor!;
        provedor.Aprovar(referencia);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = PostgresFixture.CriarContexto(o.Conexao);
            return await Montagem.Reconciliar(db, provedor).NotificacaoAsync(new NotificacaoRecebida("req-unica", referencia, "payment.updated"));
        }));

        Assert.Single(resultados, r => r == ResultadoNotificacao.Aplicada);
        await using var leitura = PostgresFixture.CriarContexto(o.Conexao);
        Assert.Equal(1, await leitura.NotificacoesPagamento.CountAsync());
        Assert.Equal(1, await leitura.Outbox.CountAsync(m => m.MessageType == "PaymentApproved.v1"));
    }

    [Fact]
    public async Task ListarParaVerificar_TrazSoOQueVenceuESemAcaoManual()
    {
        var o = await OrcamentoAprovadoAsync();
        await CobrarAsync(o, new ProvedorDeTeste());

        await using var db = PostgresFixture.CriarContexto(o.Conexao);
        var repositorio = new PagamentoRepository(db);
        var agora = DateTimeOffset.UtcNow;

        Assert.Empty(await repositorio.ListarParaVerificarAsync(agora, 10));
        var devido = Assert.Single(await repositorio.ListarParaVerificarAsync(agora.AddMinutes(3), 10));

        var pagamento = await repositorio.ObterPorIdAsync(devido);
        pagamento!.MarcarParaAcaoManual("Teste.");
        await db.SaveChangesAsync();
        Assert.Empty(await repositorio.ListarParaVerificarAsync(agora.AddMinutes(3), 10));
    }

    // Outra réplica reconcilia o mesmo pagamento entre a leitura e o SaveChanges desta notificação.
    private sealed class PagamentosComCorrida(PagamentoRepository real, Func<Task> concorrente) : IPagamentoRepository
    {
        private bool _correu;

        public async Task<Pagamento?> ObterPorIdAsync(Guid id, CancellationToken ct = default)
        {
            var pagamento = await real.ObterPorIdAsync(id, ct);
            if (!_correu)
            {
                _correu = true;
                await concorrente();
            }
            return pagamento;
        }

        public Task AdicionarAsync(Pagamento entidade, CancellationToken ct = default) => real.AdicionarAsync(entidade, ct);
        public Task<Pagamento?> ObterPorIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) => real.ObterPorIdempotencyKeyAsync(idempotencyKey, ct);
        public Task<Pagamento?> ObterPorOrcamentoAsync(Guid quoteId, int quoteVersion, CancellationToken ct = default) => real.ObterPorOrcamentoAsync(quoteId, quoteVersion, ct);
        public Task<IReadOnlyList<Pagamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default) => real.ListarPorClienteAsync(clienteId, ct);
        public Task<IReadOnlyList<Pagamento>> ListarPorOsAsync(Guid osId, CancellationToken ct = default) => real.ListarPorOsAsync(osId, ct);
        public Task<IReadOnlyList<Guid>> ListarParaVerificarAsync(DateTimeOffset agora, int limite, CancellationToken ct = default)
            => real.ListarParaVerificarAsync(agora, limite, ct);
    }

    [Fact]
    public async Task NotificacaoQuePerdeACorrida_ReleENaoDuplicaOEvento()
    {
        var o = await OrcamentoAprovadoAsync();
        var provedor = new ProvedorDeTeste();
        await CobrarAsync(o, provedor);
        Guid pagamentoId;
        string referencia;
        await using (var db = PostgresFixture.CriarContexto(o.Conexao))
        {
            var pagamento = await db.Pagamentos.SingleAsync();
            (pagamentoId, referencia) = (pagamento.Id, pagamento.ReferenciaProvedor!);
        }
        provedor.Aprovar(referencia);

        await using var contexto = PostgresFixture.CriarContexto(o.Conexao);
        var pagamentos = new PagamentosComCorrida(new PagamentoRepository(contexto), async () =>
        {
            await using var outro = PostgresFixture.CriarContexto(o.Conexao);
            await Montagem.Reconciliar(outro, provedor).ConsultarAsync(pagamentoId);
        });
        var reconciliar = new ReconciliarPagamentoUseCase(pagamentos, new NotificacaoPagamentoRepository(contexto), provedor, new OutboxRepository(contexto),
            new UnidadeDeTrabalho(contexto), Montagem.ConfiguracaoPagamento, TimeProvider.System, NullLogger<ReconciliarPagamentoUseCase>.Instance);

        var resultado = await reconciliar.NotificacaoAsync(new NotificacaoRecebida("req-corrida", referencia, "payment.updated"));

        Assert.Equal(ResultadoNotificacao.SemMudanca, resultado);
        await using var leitura = PostgresFixture.CriarContexto(o.Conexao);
        Assert.Equal(1, await leitura.Outbox.CountAsync(m => m.MessageType == "PaymentApproved.v1"));
        Assert.Equal(1, await leitura.NotificacoesPagamento.CountAsync());
    }
}
