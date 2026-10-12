using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;

namespace OficinaMecanica.Billing.IntegrationTests.Persistence;

[Collection(PostgresCollection.Nome)]
public class OrcamentoPersistenciaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task QuoteRequested_GravaOrcamentoInboxEOutboxJuntos()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var pedido = Montagem.Pedido();

        await using (var db = PostgresFixture.CriarContexto(conexao))
            Assert.Equal(StatusProcessamento.Processada, (await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(pedido))).Status);

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        var orcamento = await new OrcamentoRepository(leitura).ObterPorOsAsync(Guid.Parse(pedido["osId"]!.GetValue<string>()));
        Assert.NotNull(orcamento);
        var versao = Assert.Single(orcamento.Versoes);
        Assert.Equal(125.90m, versao.ValorTotal);
        Assert.Equal(2, versao.Itens.Count);
        // O snapshot com offset -03:00 é gravado em UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), versao.PrecoSnapshotEm);
        Assert.Equal(1, await leitura.Inbox.CountAsync());
        Assert.Equal("QuoteReady.v1", (await leitura.Outbox.SingleAsync()).MessageType);
    }

    [Fact]
    public async Task FalhaAoGravarOutbox_DesfazInboxEOrcamento()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var corpo = Montagem.Bytes(Montagem.Pedido());

        await using (var db = PostgresFixture.CriarContexto(conexao))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Montagem.Processar(db, new OutboxQueFalha()).ExecutarAsync(Montagem.CanalPedido, corpo));

        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            Assert.Equal(0, await db.Inbox.CountAsync());
            Assert.Equal(0, await db.Orcamentos.CountAsync());
        }

        // A nova tentativa da mesma mensagem é processada normalmente: nada ficou marcado como recebido.
        await using (var db = PostgresFixture.CriarContexto(conexao))
            Assert.Equal(StatusProcessamento.Processada, (await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, corpo)).Status);
    }

    [Fact]
    public async Task NovaVersaoEDecisao_PersistemComHistorico()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var clienteId = Guid.NewGuid();
        var osId = Guid.NewGuid();

        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(Montagem.Pedido(osId, clienteId)));
        }
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(Montagem.Pedido(osId, clienteId)));
        }

        Guid quoteId;
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            quoteId = (await db.Orcamentos.SingleAsync()).Id;
            var desatualizada = await Montagem.Decidir(db).ExecutarAsync(new DecidirOrcamentoCommand(quoteId, 1, ResultadoDecisao.Aprovado, clienteId, null));
            Assert.Equal(StatusTentativaDecisao.VersaoDesatualizada, desatualizada.Status);
        }
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var aprovada = await Montagem.Decidir(db).ExecutarAsync(new DecidirOrcamentoCommand(quoteId, 2, ResultadoDecisao.Aprovado, clienteId, null));
            Assert.Equal(StatusTentativaDecisao.Registrada, aprovada.Status);
        }

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        var orcamento = (await new OrcamentoRepository(leitura).ObterPorIdAsync(quoteId))!;
        Assert.Equal([StatusVersaoOrcamento.Substituida, StatusVersaoOrcamento.Aprovada], orcamento.Versoes.OrderBy(v => v.Numero).Select(v => v.Status));
        Assert.Equal(2, Assert.Single(orcamento.Decisoes).Versao);
        Assert.Equal(["QuoteReady.v1", "QuoteReady.v1", "QuoteApproved.v1"],
            await leitura.Outbox.OrderBy(m => m.CriadaEmUtc).Select(m => m.MessageType).ToListAsync());
    }

    [Fact]
    public async Task GravacaoSobreLeituraAntiga_RecebeConflito()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var clienteId = Guid.NewGuid();
        await using (var db = PostgresFixture.CriarContexto(conexao))
            await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(Montagem.Pedido(clienteId: clienteId)));

        await using var a = PostgresFixture.CriarContexto(conexao);
        await using var b = PostgresFixture.CriarContexto(conexao);
        var lidoPorA = (await new OrcamentoRepository(a).ObterPorOsAsync((await a.Orcamentos.SingleAsync()).OsId))!;
        var lidoPorB = (await new OrcamentoRepository(b).ObterPorIdAsync(lidoPorA.Id))!;

        lidoPorA.Decidir(1, ResultadoDecisao.Aprovado, clienteId, null, DateTimeOffset.UtcNow);
        await new UnidadeDeTrabalho(a).SalvarAsync();

        lidoPorB.Decidir(1, ResultadoDecisao.Recusado, clienteId, null, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ConflitoConcorrenciaException>(() => new UnidadeDeTrabalho(b).SalvarAsync());
    }

    [Fact]
    public async Task DecisoesSimultaneas_GravamUmaDecisaoEUmEvento()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var clienteId = Guid.NewGuid();
        await using (var db = PostgresFixture.CriarContexto(conexao))
            await Montagem.Processar(db).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(Montagem.Pedido(clienteId: clienteId)));
        Guid quoteId;
        await using (var db = PostgresFixture.CriarContexto(conexao))
            quoteId = (await db.Orcamentos.SingleAsync()).Id;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = PostgresFixture.CriarContexto(conexao);
            var decisao = i % 2 == 0 ? ResultadoDecisao.Aprovado : ResultadoDecisao.Recusado;
            return (decisao, (await Montagem.Decidir(db).ExecutarAsync(new DecidirOrcamentoCommand(quoteId, 1, decisao, clienteId, null))).Status);
        }));

        var vencedora = Assert.Single(resultados, r => r.Status == StatusTentativaDecisao.Registrada).decisao;
        Assert.All(resultados.Where(r => r.Status != StatusTentativaDecisao.Registrada), r =>
            Assert.Equal(r.decisao == vencedora ? StatusTentativaDecisao.JaRegistrada : StatusTentativaDecisao.DecisaoConflitante, r.Status));

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        Assert.Equal(1, await leitura.Set<DecisaoOrcamento>().CountAsync());
        Assert.Equal(1, await leitura.Outbox.CountAsync(m => m.MessageType == "QuoteApproved.v1" || m.MessageType == "QuoteDeclined.v1"));
    }

    [Fact]
    public async Task Expirar_SoListaVersoesPendentesVencidas()
    {
        var conexao = await fixture.NovoBancoMigradoAsync();
        var relogio = new RelogioFixo(DateTimeOffset.UtcNow);
        var clienteId = Guid.NewGuid();
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            await Montagem.Processar(db, relogio: relogio).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(Montagem.Pedido()));
            await Montagem.Processar(db, relogio: relogio).ExecutarAsync(Montagem.CanalPedido, Montagem.Bytes(Montagem.Pedido(clienteId: clienteId)));
        }
        await using (var db = PostgresFixture.CriarContexto(conexao))
        {
            var aprovado = await db.Orcamentos.SingleAsync(o => o.ClienteId == clienteId);
            await Montagem.Decidir(db, relogio).ExecutarAsync(new DecidirOrcamentoCommand(aprovado.Id, 1, ResultadoDecisao.Aprovado, clienteId, null));
        }

        relogio.Agora += Montagem.Validade;
        await using (var db = PostgresFixture.CriarContexto(conexao))
            Assert.Equal(1, await Montagem.Expirar(db, relogio).ExecutarAsync());
        await using (var db = PostgresFixture.CriarContexto(conexao))
            Assert.Equal(0, await Montagem.Expirar(db, relogio).ExecutarAsync());

        await using var leitura = PostgresFixture.CriarContexto(conexao);
        Assert.Equal(1, await leitura.Outbox.CountAsync(m => m.MessageType == "QuoteExpired.v1"));
    }

    private sealed class OutboxQueFalha : IOutbox
    {
        public void Adicionar(MensagemSaga mensagem) => throw new InvalidOperationException("Falha simulada na outbox.");
    }
}
