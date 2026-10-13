using System.Text.Json.Nodes;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Ports.In;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.UnitTests.Contratos;

namespace OficinaMecanica.Billing.UnitTests.UseCases;

// Cenários do CARD-39b sobre o provedor falso: idempotência, webhook, divergência, falha técnica e estorno.
public class PagamentoUseCasesTests
{
    private const string CanalCobranca = "saga-os.payment-creation-requested.v1";
    private const string CanalEstorno = "saga-os.payment-refund-requested.v1";

    private readonly ArmazenamentoEmMemoria _store = new();
    private readonly RelogioFixo _relogio = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly MontagemPagamentos _m;
    private readonly OrcamentoAprovado _orcamento = new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 125.90m, "BRL");

    public PagamentoUseCasesTests()
    {
        _m = new MontagemPagamentos(_store, _relogio);
        _m.Orcamentos.Aprovados.Add(_orcamento);
    }

    private JsonObject PedidoCobranca(Action<JsonObject>? ajustar = null)
    {
        var pedido = MensagensExemplo.Criar(CatalogoCanaisBilling.ObterConsumido(CanalCobranca)!);
        pedido["osId"] = _orcamento.OsId.ToString();
        pedido["filialId"] = _orcamento.FilialId.ToString();
        pedido["quoteId"] = _orcamento.QuoteId.ToString();
        pedido["idempotencyKey"] = $"pagamento:{_orcamento.OsId}";
        ajustar?.Invoke(pedido);
        return pedido;
    }

    private JsonObject PedidoEstorno(Pagamento pagamento, Action<JsonObject>? ajustar = null)
    {
        var pedido = MensagensExemplo.Criar(CatalogoCanaisBilling.ObterConsumido(CanalEstorno)!);
        pedido["osId"] = pagamento.OsId.ToString();
        pedido["filialId"] = pagamento.FilialId.ToString();
        pedido["paymentId"] = pagamento.Id.ToString();
        pedido["idempotencyKey"] = $"estorno:{pagamento.Id}";
        ajustar?.Invoke(pedido);
        return pedido;
    }

    private Task<ResultadoProcessamento> Processar(string canal, JsonObject pedido) => _m.Processar.ExecutarAsync(canal, MensagensExemplo.Bytes(pedido));

    private async Task<Pagamento> PagamentoPendente()
    {
        await Processar(CanalCobranca, PedidoCobranca());
        return _m.Pagamentos.Pagamentos.Single();
    }

    private async Task<Pagamento> PagamentoAprovado()
    {
        var pagamento = await PagamentoPendente();
        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Aprovado, "approved");
        await Notificar(pagamento.ReferenciaProvedor!);
        return pagamento;
    }

    private Task<ResultadoNotificacao?> Notificar(string referencia, string? requestId = null)
        => _m.Reconciliar.NotificacaoAsync(new NotificacaoRecebida(requestId ?? Guid.NewGuid().ToString(), referencia, "payment.updated"));

    private IEnumerable<T> Publicadas<T>() => _store.Publicadas.OfType<T>();

    // ── Criação ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cobranca_CriaPixComChaveDoPagamentoEPublicaPendenteComReferencia()
    {
        var pedido = PedidoCobranca();

        var resultado = await Processar(CanalCobranca, pedido);

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        var pagamento = Assert.Single(_m.Pagamentos.Pagamentos);
        var criacao = Assert.Single(_m.Provedor.Criacoes);
        Assert.Equal(pagamento.Id.ToString(), criacao.ChaveIdempotencia);
        Assert.Equal(125.90m, criacao.Valor);
        Assert.Equal(_relogio.Agora.AddMinutes(30), criacao.ExpiraEm);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Equal(_orcamento.ClienteId, pagamento.ClienteId);
        Assert.Null(pagamento.EmailPagador);

        var pendente = Assert.Single(Publicadas<PaymentPending>());
        Assert.Equal(pagamento.Id, pendente.PaymentId);
        Assert.Equal(pagamento.ReferenciaProvedor, pendente.ProviderReference);
        Assert.Equal(Guid.Parse(pedido["messageId"]!.GetValue<string>()), pendente.CausationId);
    }

    [Fact]
    public async Task MesmaMensagemDuasVezes_UmaCobranca()
    {
        var pedido = PedidoCobranca();

        await Processar(CanalCobranca, pedido);
        var segunda = await Processar(CanalCobranca, pedido);

        Assert.Equal(StatusProcessamento.Duplicada, segunda.Status);
        Assert.Single(_m.Provedor.Criacoes);
        Assert.Single(Publicadas<PaymentPending>());
    }

    [Fact]
    public async Task MesmaChaveEmOutraMensagem_NaoCobraDeNovoERetornaAMesmaReferencia()
    {
        await Processar(CanalCobranca, PedidoCobranca());
        var resultado = await Processar(CanalCobranca, PedidoCobranca());

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        Assert.Single(_m.Pagamentos.Pagamentos);
        Assert.Single(_m.Provedor.Criacoes);
        var pendentes = Publicadas<PaymentPending>().ToList();
        Assert.Equal(2, pendentes.Count);
        Assert.Single(pendentes.Select(p => (p.PaymentId, p.ProviderReference)).Distinct());
    }

    public static TheoryData<string> ComandosQueNaoCobram() => new()
    {
        "orçamento não aprovado", "valor diferente", "moeda diferente", "OS diferente", "sem e-mail", "outra chave para a mesma versão"
    };

    [Theory]
    [MemberData(nameof(ComandosQueNaoCobram))]
    public async Task ComandoQueNaoPodeCobrar_VaiParaDlqSemChamarOProvedor(string caso)
    {
        if (caso == "outra chave para a mesma versão")
        {
            await Processar(CanalCobranca, PedidoCobranca());
            _store.Publicadas.Clear();
            _m.Provedor.Criacoes.Clear();
        }

        var pedido = PedidoCobranca(p =>
        {
            switch (caso)
            {
                case "orçamento não aprovado": p["quoteVersion"] = 2; break;
                case "valor diferente": p["amount"] = 99.90m; break;
                case "moeda diferente": p["currency"] = "USD"; break;
                case "OS diferente": p["osId"] = Guid.NewGuid().ToString(); break;
                case "sem e-mail": p.Remove("payerEmail"); break;
                case "outra chave para a mesma versão": p["idempotencyKey"] = "outra-chave"; break;
            }
        });

        var resultado = await Processar(CanalCobranca, pedido);

        Assert.Equal(StatusProcessamento.Rejeitada, resultado.Status);
        Assert.Empty(_m.Provedor.Criacoes);
        Assert.Empty(_store.Publicadas);
    }

    // ── Falha técnica e reconciliação ─────────────────────────────────────────

    [Fact]
    public async Task CriacaoSemResposta_PublicaResultadoDesconhecidoENaoRecusa()
    {
        _m.Provedor.FalhasCriacao.Enqueue("Timeout.");

        var resultado = await Processar(CanalCobranca, PedidoCobranca());

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        Assert.Equal(StatusPagamento.Solicitado, _m.Pagamentos.Pagamentos.Single().Status);
        Assert.Single(Publicadas<PaymentOutcomeUnknown>());
        Assert.Empty(Publicadas<PaymentDeclined>());
        Assert.Empty(Publicadas<PaymentApproved>());
    }

    [Fact]
    public async Task ReconciliacaoDepoisDaFalha_RefazACriacaoComAMesmaChave()
    {
        _m.Provedor.FalhasCriacao.Enqueue("Timeout.");
        await Processar(CanalCobranca, PedidoCobranca());

        _relogio.Agora = _relogio.Agora.AddMinutes(3);
        await _m.Verificar.ExecutarAsync();

        var pagamento = _m.Pagamentos.Pagamentos.Single();
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Equal(2, _m.Provedor.Criacoes.Count);
        Assert.Single(_m.Provedor.Criacoes.Select(c => c.ChaveIdempotencia).Distinct());
        Assert.Single(Publicadas<PaymentOutcomeUnknown>());
        Assert.Single(Publicadas<PaymentPending>());
    }

    [Fact]
    public async Task CobrancaQueChegouAoProvedorSemResposta_EncontradaNaBuscaSemCriarDeNovo()
    {
        _m.Provedor.FalhasCriacao.Enqueue("Timeout.");
        await Processar(CanalCobranca, PedidoCobranca());
        var pagamento = _m.Pagamentos.Pagamentos.Single();
        _m.Provedor.CriarSemResponder(pagamento.Id, pagamento.Valor);

        _relogio.Agora = _relogio.Agora.AddMinutes(3);
        await _m.Verificar.ExecutarAsync();

        Assert.Single(_m.Provedor.Criacoes);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
    }

    [Fact]
    public async Task ProvedorIndisponivelAteOLimite_FicaParaAcaoManualSemRecusar()
    {
        for (var i = 0; i < MontagemPagamentos.Configuracao.MaxTentativas; i++)
            _m.Provedor.FalhasCriacao.Enqueue("HTTP 503.");
        await Processar(CanalCobranca, PedidoCobranca());

        for (var i = 1; i < MontagemPagamentos.Configuracao.MaxTentativas; i++)
        {
            _relogio.Agora = _relogio.Agora.AddMinutes(3);
            await _m.Verificar.ExecutarAsync();
        }

        var pagamento = _m.Pagamentos.Pagamentos.Single();
        Assert.True(pagamento.ReconciliacaoManual);
        Assert.Equal(StatusPagamento.Solicitado, pagamento.Status);
        Assert.Empty(Publicadas<PaymentDeclined>());

        _relogio.Agora = _relogio.Agora.AddMinutes(3);
        Assert.Equal(0, await _m.Verificar.ExecutarAsync());
    }

    [Fact]
    public async Task ExcecaoAoChamarOProvedor_NaoDevolveAMensagemEDeixaParaAReconciliacao()
    {
        _m.Provedor.LancarNaCriacao = true;

        var resultado = await Processar(CanalCobranca, PedidoCobranca());

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        var pagamento = _m.Pagamentos.Pagamentos.Single();
        Assert.Equal(StatusPagamento.Solicitado, pagamento.Status);
        Assert.NotNull(pagamento.ProximaVerificacaoEm);
    }

    [Fact]
    public async Task PendenteSemNotificacao_ReconciliacaoConsultaEAplicaAprovacao()
    {
        var pagamento = await PagamentoPendente();
        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Aprovado, "approved");

        _relogio.Agora = _relogio.Agora.AddMinutes(3);
        await _m.Verificar.ExecutarAsync();

        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
        Assert.Single(Publicadas<PaymentApproved>());
    }

    // ── Webhook ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotificacaoDeAprovacao_ConsultaOProvedorEPublicaUmaVez()
    {
        var pagamento = await PagamentoPendente();
        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Aprovado, "approved");

        var primeira = await Notificar(pagamento.ReferenciaProvedor!);
        var outraRequisicao = await Notificar(pagamento.ReferenciaProvedor!);

        Assert.Equal(ResultadoNotificacao.Aplicada, primeira);
        Assert.Equal(ResultadoNotificacao.SemMudanca, outraRequisicao);
        var aprovado = Assert.Single(Publicadas<PaymentApproved>());
        Assert.Equal(125.90m, aprovado.Amount);
        Assert.Null(aprovado.CausationId);
        Assert.Equal(2, _m.Pagamentos.Notificacoes.Count);
    }

    [Fact]
    public async Task MesmaRequisicaoReenviada_NaoEProcessadaDeNovo()
    {
        var pagamento = await PagamentoPendente();

        await Notificar(pagamento.ReferenciaProvedor!, "req-1");
        var repetida = await Notificar(pagamento.ReferenciaProvedor!, "req-1");

        Assert.Null(repetida);
        Assert.Single(_m.Pagamentos.Notificacoes);
    }

    [Fact]
    public async Task NotificacaoPendenteDepoisDeAprovado_NaoRegrideNemPublica()
    {
        var pagamento = await PagamentoAprovado();
        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Pendente, "pending");

        var resultado = await Notificar(pagamento.ReferenciaProvedor!);

        Assert.Equal(ResultadoNotificacao.SemMudanca, resultado);
        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
        Assert.Single(Publicadas<PaymentApproved>());
    }

    [Fact]
    public async Task ValorDivergenteNoProvedor_NaoAprovaNemPublica()
    {
        var pagamento = await PagamentoPendente();
        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Aprovado, "approved", valor: 1m);

        var resultado = await Notificar(pagamento.ReferenciaProvedor!);

        Assert.Equal(ResultadoNotificacao.Divergente, resultado);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Empty(Publicadas<PaymentApproved>());
        Assert.Equal(pagamento.Id, _m.Pagamentos.Notificacoes.Single().PagamentoId);
    }

    [Fact]
    public async Task PagamentoDesconhecido_NaoAlteraNada()
    {
        await PagamentoPendente();
        _m.Provedor.CriarSemResponder(Guid.NewGuid(), 10m);
        var referenciaAlheia = _m.Provedor.Pagamentos.Keys.Max()!;

        var alheio = await Notificar(referenciaAlheia);
        var inexistente = await Notificar("999999");

        Assert.Equal(ResultadoNotificacao.Desconhecido, alheio);
        Assert.Equal(ResultadoNotificacao.Desconhecido, inexistente);
        Assert.Single(Publicadas<PaymentPending>());
        Assert.Equal(StatusPagamento.Pendente, _m.Pagamentos.Pagamentos.Single().Status);
    }

    [Fact]
    public async Task ConsultaFalhaNoWebhook_NaoTrataComoRecusa()
    {
        var pagamento = await PagamentoPendente();
        _m.Provedor.FalhasConsulta.Enqueue("Timeout.");

        var resultado = await Notificar(pagamento.ReferenciaProvedor!);

        Assert.Equal(ResultadoNotificacao.FalhaTecnica, resultado);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Empty(Publicadas<PaymentDeclined>());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task RecusaNoProvedor_PublicaRecusaSemAprovacao(string status)
    {
        var pagamento = await PagamentoPendente();
        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Recusado, status);

        await Notificar(pagamento.ReferenciaProvedor!);

        Assert.Equal(StatusPagamento.Recusado, pagamento.Status);
        Assert.Contains(status, Assert.Single(Publicadas<PaymentDeclined>()).Reason);
        Assert.Empty(Publicadas<PaymentApproved>());
    }

    // ── Estorno ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Estorno_ChamaOProvedorComChaveDoPagamentoEPublicaEstornado()
    {
        var pagamento = await PagamentoAprovado();
        var pedido = PedidoEstorno(pagamento);

        var resultado = await Processar(CanalEstorno, pedido);

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        Assert.Equal([$"estorno-{pagamento.Id}"], _m.Provedor.ChavesEstorno);
        Assert.Equal(StatusPagamento.Estornado, pagamento.Status);
        var estornado = Assert.Single(Publicadas<PaymentRefunded>());
        Assert.Equal(Guid.Parse(pedido["messageId"]!.GetValue<string>()), estornado.CausationId);
    }

    [Fact]
    public async Task EstornoRepetido_RetornaOResultadoGravadoSemSegundoEstorno()
    {
        var pagamento = await PagamentoAprovado();
        await Processar(CanalEstorno, PedidoEstorno(pagamento));

        var resultado = await Processar(CanalEstorno, PedidoEstorno(pagamento));

        Assert.Equal(StatusProcessamento.Processada, resultado.Status);
        Assert.Single(_m.Provedor.ChavesEstorno);
        Assert.Equal(2, Publicadas<PaymentRefunded>().Count());
    }

    [Fact]
    public async Task EstornoEmProcessamento_PublicaPendenteEConfirmaNaReconciliacao()
    {
        var pagamento = await PagamentoAprovado();
        _m.Provedor.RespostasEstorno.Enqueue(ResultadoProvedor<EstornoNoProvedor>.Sucesso(
            new EstornoNoProvedor("r-1", StatusEstornoNoProvedor.Pendente, "in_process")));

        await Processar(CanalEstorno, PedidoEstorno(pagamento));
        Assert.Single(Publicadas<PaymentRefundPending>());
        Assert.Empty(Publicadas<PaymentRefunded>());

        _m.Provedor.Mudar(pagamento.ReferenciaProvedor!, StatusNoProvedor.Estornado, "refunded");
        _relogio.Agora = _relogio.Agora.AddMinutes(3);
        await _m.Verificar.ExecutarAsync();

        Assert.Equal(StatusPagamento.Estornado, pagamento.Status);
        Assert.Single(Publicadas<PaymentRefunded>());
        Assert.Single(_m.Provedor.ChavesEstorno);
    }

    [Fact]
    public async Task EstornoSemRespostaAteOLimite_PublicaFalhaDoEstorno()
    {
        var pagamento = await PagamentoAprovado();
        for (var i = 0; i < MontagemPagamentos.Configuracao.MaxTentativas; i++)
            _m.Provedor.RespostasEstorno.Enqueue(ResultadoProvedor<EstornoNoProvedor>.FalhaTecnica("HTTP 500."));

        await Processar(CanalEstorno, PedidoEstorno(pagamento));
        for (var i = 1; i < MontagemPagamentos.Configuracao.MaxTentativas; i++)
        {
            _relogio.Agora = _relogio.Agora.AddMinutes(3);
            await _m.Verificar.ExecutarAsync();
        }

        Assert.Equal(StatusPagamento.EstornoFalhou, pagamento.Status);
        Assert.Single(Publicadas<PaymentRefundPending>());
        Assert.Single(Publicadas<PaymentRefundFailed>());
        Assert.Empty(Publicadas<PaymentRefunded>());
        Assert.Single(_m.Provedor.ChavesEstorno.Distinct());
    }

    [Fact]
    public async Task EstornoDePagamentoInexistenteOuNaoAprovado_VaiParaDlq()
    {
        var pendente = await PagamentoPendente();

        var naoAprovado = await Processar(CanalEstorno, PedidoEstorno(pendente));
        var inexistente = await Processar(CanalEstorno, PedidoEstorno(pendente, p => p["paymentId"] = Guid.NewGuid().ToString()));

        Assert.Equal(StatusProcessamento.Rejeitada, naoAprovado.Status);
        Assert.Equal(StatusProcessamento.Rejeitada, inexistente.Status);
        Assert.Empty(_m.Provedor.ChavesEstorno);
    }
}
