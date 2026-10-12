using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.UnitTests.Contratos;

namespace OficinaMecanica.Billing.UnitTests.UseCases;

public class OrcamentoUseCasesTests
{
    private const string Canal = "saga-os.quote-requested.v1";
    private static readonly TimeSpan _validade = TimeSpan.FromHours(24);

    private readonly ArmazenamentoEmMemoria _store = new();
    private readonly RelogioFixo _relogio = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly SolicitarOrcamentoUseCase _solicitar;
    private readonly ProcessarMensagemSagaUseCase _processar;
    private readonly DecidirOrcamentoUseCase _decidir;
    private readonly ExpirarOrcamentosUseCase _expirar;
    private readonly ConsultarOrcamentosUseCase _consultar;

    public OrcamentoUseCasesTests()
    {
        _solicitar = new SolicitarOrcamentoUseCase(_store, new ConfiguracaoOrcamento(_validade), _relogio);
        _processar = new ProcessarMensagemSagaUseCase(_store, _store, _store, _store, _solicitar, NullLogger<ProcessarMensagemSagaUseCase>.Instance);
        _decidir = new DecidirOrcamentoUseCase(_store, _store, _store, _relogio, NullLogger<DecidirOrcamentoUseCase>.Instance);
        _expirar = new ExpirarOrcamentosUseCase(_store, _store, _store, _relogio, NullLogger<ExpirarOrcamentosUseCase>.Instance);
        _consultar = new ConsultarOrcamentosUseCase(_store);
    }

    private static JsonObject Pedido(Action<JsonObject>? ajustar = null)
    {
        var pedido = MensagensExemplo.Criar(CatalogoCanaisBilling.ObterConsumido(Canal)!);
        ajustar?.Invoke(pedido);
        return pedido;
    }

    private Task<ResultadoProcessamento> Processar(JsonObject pedido) => _processar.ExecutarAsync(Canal, MensagensExemplo.Bytes(pedido));

    private async Task<Orcamento> OrcamentoCriado()
    {
        await Processar(Pedido());
        return _store.Orcamentos.Single();
    }

    // ── QuoteRequested ────────────────────────────────────────────────────────

    [Fact]
    public async Task QuoteRequested_CriaOrcamentoEPublicaQuoteReadyComTotalDoSnapshot()
    {
        var pedido = Pedido();

        var resultado = await Processar(pedido);

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        var orcamento = Assert.Single(_store.Orcamentos);
        var pronto = Assert.IsType<QuoteReady>(Assert.Single(_store.Publicadas));
        Assert.Equal((orcamento.Id, 1, 125.90m, "BRL"), (pronto.QuoteId, pronto.QuoteVersion, pronto.Amount, pronto.Currency));
        Assert.Equal(_relogio.Agora + _validade, pronto.ExpiresAtUtc);
        Assert.Equal(Guid.Parse(pedido["messageId"]!.GetValue<string>()), pronto.CausationId);
        Assert.Equal(Guid.Parse(pedido["clienteId"]!.GetValue<string>()), orcamento.ClienteId);
    }

    [Fact]
    public async Task MesmaMensagemReentregue_EDuplicadaSemNovoEvento()
    {
        var pedido = Pedido();
        await Processar(pedido);

        var segunda = await Processar(pedido);

        Assert.Equal(StatusProcessamento.Duplicada, segunda.Status);
        Assert.Single(_store.Publicadas);
    }

    [Fact]
    public async Task OutraMensagemComMesmaChave_DevolveAMesmaVersao()
    {
        var pedido = Pedido();
        await Processar(pedido);

        await Processar(Pedido(p =>
        {
            p["osId"] = pedido["osId"]!.DeepClone();
            p["clienteId"] = pedido["clienteId"]!.DeepClone();
            p["idempotencyKey"] = pedido["idempotencyKey"]!.DeepClone();
        }));

        Assert.Single(_store.Orcamentos.Single().Versoes);
        Assert.All(_store.Publicadas, e => Assert.Equal(1, Assert.IsType<QuoteReady>(e).QuoteVersion));
    }

    [Fact]
    public async Task NovaChaveParaOsPendente_CriaVersao2()
    {
        var pedido = Pedido();
        await Processar(pedido);

        await Processar(Pedido(p => { p["osId"] = pedido["osId"]!.DeepClone(); p["clienteId"] = pedido["clienteId"]!.DeepClone(); }));

        Assert.Equal(2, Assert.IsType<QuoteReady>(_store.Publicadas[^1]).QuoteVersion);
        Assert.Equal(StatusVersaoOrcamento.Substituida, _store.Orcamentos.Single().Versoes.Single(v => v.Numero == 1).Status);
    }

    [Fact]
    public async Task NovoPedidoDepoisDaDecisao_PublicaQuoteRejected()
    {
        var orcamento = await OrcamentoCriado();
        await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, orcamento.ClienteId, null));

        await Processar(Pedido(p => { p["osId"] = orcamento.OsId.ToString(); p["clienteId"] = orcamento.ClienteId.ToString(); }));

        Assert.IsType<QuoteRejected>(_store.Publicadas[^1]);
        Assert.Single(orcamento.Versoes);
    }

    [Fact]
    public async Task PedidoDeOutroClienteParaAMesmaOs_PublicaQuoteRejected()
    {
        var orcamento = await OrcamentoCriado();

        await Processar(Pedido(p => p["osId"] = orcamento.OsId.ToString()));

        Assert.IsType<QuoteRejected>(_store.Publicadas[^1]);
        Assert.Single(orcamento.Versoes);
    }

    public static TheoryData<string> PedidosDeNegocioInvalidos() => new() { "sem preço", "preço zero", "sem moeda", "moeda USD", "sem itens" };

    [Theory]
    [MemberData(nameof(PedidosDeNegocioInvalidos))]
    public async Task PedidoInvalidoDeNegocio_PublicaQuoteRejectedSemCriarOrcamento(string caso)
    {
        var resultado = await Processar(Pedido(p =>
        {
            var item = (JsonObject)p["items"]![0]!;
            switch (caso)
            {
                case "sem preço": item.Remove("unitPrice"); break;
                case "preço zero": item["unitPrice"] = 0; break;
                case "sem moeda": p.Remove("currency"); break;
                case "moeda USD": p["currency"] = "USD"; break;
                case "sem itens": p["items"] = new JsonArray(); break;
            }
        }));

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<QuoteRejected>(Assert.Single(_store.Publicadas)).Reason));
        Assert.Empty(_store.Orcamentos);
    }

    [Fact]
    public async Task MensagemForaDoContrato_ERejeitadaParaADlq()
    {
        var resultado = await Processar(Pedido(p => p["clienteId"] = "cliente"));

        Assert.Equal(StatusProcessamento.Rejeitada, resultado.Status);
        Assert.Empty(_store.Publicadas);
    }

    [Fact]
    public async Task CanalDesconhecido_ERejeitado()
    {
        var resultado = await _processar.ExecutarAsync("saga-os.payment-creation-requested.v1", MensagensExemplo.Bytes(Pedido()));

        Assert.Equal(StatusProcessamento.Rejeitada, resultado.Status);
    }

    // ── Decisão do cliente ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ResultadoDecisao.Aprovado, typeof(QuoteApproved), StatusVersaoOrcamento.Aprovada)]
    [InlineData(ResultadoDecisao.Recusado, typeof(QuoteDeclined), StatusVersaoOrcamento.Recusada)]
    public async Task Decidir_RegistraEPublicaOEventoUmaVez(ResultadoDecisao decisao, Type evento, StatusVersaoOrcamento status)
    {
        var orcamento = await OrcamentoCriado();
        _store.Publicadas.Clear();

        var resultado = await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, decisao, orcamento.ClienteId, "motivo"));
        var repetida = await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, decisao, orcamento.ClienteId, "motivo"));

        Assert.Equal(StatusTentativaDecisao.Registrada, resultado.Status);
        Assert.Equal(status, resultado.Orcamento.VersaoVigente.Status);
        Assert.Equal(StatusTentativaDecisao.JaRegistrada, repetida.Status);
        var publicado = Assert.Single(_store.Publicadas);
        Assert.IsType(evento, publicado);
        Assert.Equal(orcamento.CorrelationId, publicado.CorrelationId);
    }

    [Fact]
    public async Task Decidir_OrcamentoDeOutroCliente_RespondeNaoEncontradoSemMudarNada()
    {
        var orcamento = await OrcamentoCriado();
        _store.Publicadas.Clear();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, Guid.NewGuid(), null)));

        Assert.Equal(StatusVersaoOrcamento.AguardandoDecisao, orcamento.VersaoVigente.Status);
        Assert.Empty(orcamento.Decisoes);
        Assert.Empty(_store.Publicadas);
    }

    [Fact]
    public async Task Decidir_OrcamentoInexistente_RespondeNaoEncontrado()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _decidir.ExecutarAsync(new DecidirOrcamentoCommand(Guid.NewGuid(), 1, ResultadoDecisao.Aprovado, Guid.NewGuid(), null)));
    }

    [Fact]
    public async Task Decidir_DepoisDaValidade_ExpiraEPublicaQuoteExpiredUmaVez()
    {
        var orcamento = await OrcamentoCriado();
        _store.Publicadas.Clear();
        _relogio.Agora += _validade;

        var primeira = await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, orcamento.ClienteId, null));
        var segunda = await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, orcamento.ClienteId, null));

        Assert.Equal(StatusTentativaDecisao.Expirada, primeira.Status);
        Assert.Equal(StatusTentativaDecisao.Expirada, segunda.Status);
        Assert.IsType<QuoteExpired>(Assert.Single(_store.Publicadas));
    }

    [Fact]
    public async Task Decidir_VersaoAntiga_EDesatualizadaSemEvento()
    {
        var orcamento = await OrcamentoCriado();
        await Processar(Pedido(p => { p["osId"] = orcamento.OsId.ToString(); p["clienteId"] = orcamento.ClienteId.ToString(); }));
        _store.Publicadas.Clear();

        var resultado = await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, orcamento.ClienteId, null));

        Assert.Equal(StatusTentativaDecisao.VersaoDesatualizada, resultado.Status);
        Assert.Equal(2, resultado.Orcamento.VersaoVigente.Versao);
        Assert.Empty(_store.Publicadas);
    }

    // ── Expiração ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expirar_SoVersoesPendentesVencidas()
    {
        var pendente = await OrcamentoCriado();
        await Processar(Pedido());
        var aprovado = _store.Orcamentos.Single(o => o.Id != pendente.Id);
        await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(aprovado.Id, 1, ResultadoDecisao.Aprovado, aprovado.ClienteId, null));
        _store.Publicadas.Clear();

        Assert.Equal(0, await _expirar.ExecutarAsync());
        _relogio.Agora += _validade;
        Assert.Equal(1, await _expirar.ExecutarAsync());
        Assert.Equal(0, await _expirar.ExecutarAsync());

        var expirado = Assert.IsType<QuoteExpired>(Assert.Single(_store.Publicadas));
        Assert.Equal(pendente.Id, expirado.QuoteId);
        Assert.Equal(StatusVersaoOrcamento.Aprovada, aprovado.VersaoVigente.Status);
    }

    // ── Consultas e porta do pagamento ────────────────────────────────────────

    [Fact]
    public async Task Consultar_ClienteSoVeOsProprios()
    {
        var orcamento = await OrcamentoCriado();
        await Processar(Pedido());

        Assert.Single(await _consultar.ListarDoClienteAsync(orcamento.ClienteId));
        Assert.Equal(orcamento.Id, (await _consultar.ObterAsync(orcamento.Id, orcamento.ClienteId)).QuoteId);
        await Assert.ThrowsAsync<NotFoundException>(() => _consultar.ObterAsync(orcamento.Id, Guid.NewGuid()));
        Assert.Equal(orcamento.Id, (await _consultar.ObterAsync(orcamento.Id, null)).QuoteId);
        Assert.Equal(orcamento.Id, (await _consultar.ObterPorOsAsync(orcamento.OsId)).QuoteId);
    }

    [Fact]
    public async Task PortaDoPagamento_SoDevolveVersaoAprovada()
    {
        var orcamento = await OrcamentoCriado();

        Assert.Null(await _consultar.ObterVersaoAprovadaAsync(orcamento.Id, 1));
        await _decidir.ExecutarAsync(new DecidirOrcamentoCommand(orcamento.Id, 1, ResultadoDecisao.Aprovado, orcamento.ClienteId, null));

        var aprovado = await _consultar.ObterVersaoAprovadaAsync(orcamento.Id, 1);
        Assert.NotNull(aprovado);
        Assert.Equal((orcamento.OsId, 125.90m, "BRL"), (aprovado.OsId, aprovado.Valor, aprovado.Moeda));
        Assert.Null(await _consultar.ObterVersaoAprovadaAsync(orcamento.Id, 2));
    }
}
