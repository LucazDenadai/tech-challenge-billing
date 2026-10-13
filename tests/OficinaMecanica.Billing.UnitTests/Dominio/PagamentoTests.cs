using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.UnitTests.Dominio;

public class PagamentoTests
{
    private static readonly DateTimeOffset _agora = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _expiracao = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _intervalo = TimeSpan.FromMinutes(2);
    private const int MaxTentativas = 3;

    private static Pagamento Criar(decimal valor = 125.90m, string? email = "cliente@exemplo.com")
        => Pagamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            valor, "BRL", "pagamento-1", Guid.NewGuid(), email, _agora, _expiracao, _intervalo);

    private static PagamentoNoProvedor NoProvedor(Pagamento pagamento, StatusNoProvedor status, string original,
        string referencia = "123456", decimal? valor = null, string moeda = "BRL", string? referenciaExterna = null)
        => new(referencia, referenciaExterna ?? pagamento.Id.ToString(), valor ?? pagamento.Valor, moeda, status, original,
            status == StatusNoProvedor.Aprovado ? _agora.AddMinutes(5) : null, "00020126pix", "https://mp.exemplo/pix");

    private static Pagamento Aprovado()
    {
        var pagamento = Criar();
        pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Pendente, "pending"), _agora, _intervalo);
        pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved"), _agora, _intervalo);
        return pagamento;
    }

    private static Pagamento ComEstornoPedido()
    {
        var pagamento = Aprovado();
        pagamento.SolicitarEstorno("estorno-1", Guid.NewGuid(), pagamento.Valor, "BRL", "Execução falhou.", _agora, _intervalo);
        return pagamento;
    }

    // ── Criação ───────────────────────────────────────────────────────────────

    [Fact]
    public void Criar_ComecaSolicitadoComExpiracaoEVerificacaoAgendada()
    {
        var pagamento = Criar();

        Assert.Equal(StatusPagamento.Solicitado, pagamento.Status);
        Assert.True(pagamento.PrecisaCriarNoProvedor);
        Assert.Equal(_agora + _expiracao, pagamento.ExpiraEm);
        Assert.Equal(_agora + _intervalo, pagamento.ProximaVerificacaoEm);
        Assert.Equal("cliente@exemplo.com", pagamento.EmailPagador);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.123)]
    public void Criar_ValorInvalido_Recusa(decimal valor)
        => Assert.Throws<PagamentoInvalidoException>(() => Criar(valor));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Criar_SemEmailDoPagador_Recusa(string? email)
        => Assert.Throws<PagamentoInvalidoException>(() => Criar(email: email));

    [Fact]
    public void Criar_MoedaDiferenteDeBrl_Recusa()
        => Assert.Throws<PagamentoInvalidoException>(() => Pagamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 1, 10m, "USD", "k", Guid.NewGuid(), "a@b.c", _agora, _expiracao, _intervalo));

    // ── Reconciliação ─────────────────────────────────────────────────────────

    [Fact]
    public void Pendente_GravaReferenciaECodigoPixEApagaOEmail()
    {
        var pagamento = Criar();

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Pendente, "pending"), _agora, _intervalo);

        Assert.Equal(new Reconciliacao(ResultadoReconciliacao.Transicao, EventoPagamento.Pendente), resultado);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Equal("123456", pagamento.ReferenciaProvedor);
        Assert.Equal("00020126pix", pagamento.CodigoPix);
        Assert.Null(pagamento.EmailPagador);
        Assert.Equal(_agora + _intervalo, pagamento.ProximaVerificacaoEm);
    }

    [Fact]
    public void Aprovado_PublicaUmaVezENaoAgendaVerificacao()
    {
        var pagamento = Criar();
        pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Pendente, "pending"), _agora, _intervalo);

        var primeira = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved"), _agora, _intervalo);
        var repetida = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved"), _agora, _intervalo);

        Assert.Equal(EventoPagamento.Aprovado, primeira.Evento);
        Assert.Equal(ResultadoReconciliacao.SemMudanca, repetida.Resultado);
        Assert.Null(repetida.Evento);
        Assert.Equal(_agora.AddMinutes(5), pagamento.AprovadoEm);
        Assert.Null(pagamento.ProximaVerificacaoEm);
    }

    [Fact]
    public void AprovadoDiretoDeSolicitado_PublicaAprovado()
    {
        // A criação ficou sem resposta e a reconciliação já encontrou o pagamento aprovado.
        var pagamento = Criar();

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved"), _agora, _intervalo);

        Assert.Equal(EventoPagamento.Aprovado, resultado.Evento);
        Assert.Equal("123456", pagamento.ReferenciaProvedor);
    }

    [Fact]
    public void NotificacaoPendenteDepoisDeAprovado_NaoRegride()
    {
        var pagamento = Aprovado();

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Pendente, "pending"), _agora, _intervalo);

        Assert.Equal(ResultadoReconciliacao.SemMudanca, resultado.Resultado);
        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public void Recusado_PublicaRecusaComMotivo(string statusOriginal)
    {
        var pagamento = Criar();
        pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Pendente, "pending"), _agora, _intervalo);

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Recusado, statusOriginal), _agora, _intervalo);

        Assert.Equal(EventoPagamento.Recusado, resultado.Evento);
        Assert.Equal(StatusPagamento.Recusado, pagamento.Status);
        Assert.Contains(statusOriginal, pagamento.MotivoRecusa);
        Assert.Null(pagamento.ProximaVerificacaoEm);
    }

    [Fact]
    public void AprovacaoDepoisDeRecusa_NaoRegride()
    {
        var pagamento = Criar();
        pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Recusado, "rejected"), _agora, _intervalo);

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved"), _agora, _intervalo);

        Assert.Equal(ResultadoReconciliacao.SemMudanca, resultado.Resultado);
        Assert.Equal(StatusPagamento.Recusado, pagamento.Status);
    }

    public static TheoryData<string> Divergencias() => new() { "valor", "moeda", "referencia-externa", "outro-pagamento" };

    [Theory]
    [MemberData(nameof(Divergencias))]
    public void Divergencia_NaoMudaEstadoNemPublica(string caso)
    {
        var pagamento = Criar();
        pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Pendente, "pending"), _agora, _intervalo);

        var consultado = caso switch
        {
            "valor" => NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved", valor: 1m),
            "moeda" => NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved", moeda: "USD"),
            "referencia-externa" => NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved", referenciaExterna: Guid.NewGuid().ToString()),
            "outro-pagamento" => NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved", referencia: "999"),
            _ => throw new ArgumentOutOfRangeException(nameof(caso))
        };

        var resultado = pagamento.Reconciliar(consultado, _agora, _intervalo);

        Assert.Equal(ResultadoReconciliacao.Divergente, resultado.Resultado);
        Assert.Null(resultado.Evento);
        Assert.Equal(StatusPagamento.Pendente, pagamento.Status);
        Assert.Null(pagamento.AprovadoEm);
    }

    [Theory]
    [InlineData(StatusNoProvedor.ForaDoFluxo, "charged_back")]
    [InlineData(StatusNoProvedor.Estornado, "refunded")]
    [InlineData(StatusNoProvedor.Recusado, "cancelled")]
    public void StatusForaDoFluxoDepoisDeAprovado_MarcaAcaoManualSemMudarEstado(StatusNoProvedor status, string original)
    {
        var pagamento = Aprovado();

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, status, original), _agora, _intervalo);

        Assert.Equal(ResultadoReconciliacao.ForaDoFluxo, resultado.Resultado);
        Assert.Null(resultado.Evento);
        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
        Assert.True(pagamento.ReconciliacaoManual);
    }

    // ── Falha técnica ─────────────────────────────────────────────────────────

    [Fact]
    public void FalhaTecnicaNaCriacao_PublicaResultadoDesconhecidoSoNaPrimeira()
    {
        var pagamento = Criar();

        var primeira = pagamento.RegistrarFalhaTecnica(_agora, _intervalo, MaxTentativas);
        var segunda = pagamento.RegistrarFalhaTecnica(_agora, _intervalo, MaxTentativas);

        Assert.Equal(EventoPagamento.ResultadoDesconhecido, primeira);
        Assert.Null(segunda);
        Assert.Equal(StatusPagamento.Solicitado, pagamento.Status);
        Assert.Equal(2, pagamento.FalhasTecnicas);
        Assert.False(pagamento.ReconciliacaoManual);
    }

    [Fact]
    public void FalhaTecnicaAteOLimite_MarcaAcaoManualSemRecusar()
    {
        var pagamento = Criar();

        for (var i = 0; i < MaxTentativas; i++)
            pagamento.RegistrarFalhaTecnica(_agora, _intervalo, MaxTentativas);

        Assert.Equal(StatusPagamento.Solicitado, pagamento.Status);
        Assert.True(pagamento.ReconciliacaoManual);
        Assert.Null(pagamento.ProximaVerificacaoEm);
    }

    [Fact]
    public void RespostaDoProvedorDepoisDeFalhas_ZeraContagemEResolveAcaoManual()
    {
        var pagamento = Criar();
        for (var i = 0; i < MaxTentativas; i++)
            pagamento.RegistrarFalhaTecnica(_agora, _intervalo, MaxTentativas);

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Aprovado, "approved"), _agora, _intervalo);

        Assert.Equal(EventoPagamento.Aprovado, resultado.Evento);
        Assert.Equal(0, pagamento.FalhasTecnicas);
        Assert.False(pagamento.ReconciliacaoManual);
    }

    // ── Estorno ───────────────────────────────────────────────────────────────

    [Fact]
    public void SolicitarEstorno_DeAprovado_FicaPendenteDeChamada()
    {
        var pagamento = Aprovado();

        var resultado = pagamento.SolicitarEstorno("estorno-1", Guid.NewGuid(), pagamento.Valor, "BRL", "Execução falhou.", _agora, _intervalo);

        Assert.Equal(ResultadoPedidoEstorno.Aceito, resultado);
        Assert.Equal(StatusPagamento.EstornoPendente, pagamento.Status);
        Assert.True(pagamento.PrecisaPedirEstorno);
    }

    [Fact]
    public void SolicitarEstorno_Repetido_DevolveOGravado()
    {
        var pagamento = ComEstornoPedido();
        pagamento.RegistrarEstorno(new EstornoNoProvedor("r-1", StatusEstornoNoProvedor.Aprovado, "approved"), _agora, _intervalo);

        var resultado = pagamento.SolicitarEstorno("estorno-1", Guid.NewGuid(), pagamento.Valor, "BRL", "Execução falhou.", _agora, _intervalo);

        Assert.Equal(ResultadoPedidoEstorno.JaSolicitado, resultado);
        Assert.Equal(EventoPagamento.Estornado, pagamento.EventoDoEstornoAtual);
    }

    [Fact]
    public void SolicitarEstorno_DePagamentoNaoAprovado_Recusa()
    {
        var pagamento = Criar();

        Assert.Throws<PagamentoInvalidoException>(() =>
            pagamento.SolicitarEstorno("estorno-1", Guid.NewGuid(), pagamento.Valor, "BRL", "x", _agora, _intervalo));
    }

    [Fact]
    public void SolicitarEstorno_Parcial_Recusa()
    {
        var pagamento = Aprovado();

        Assert.Throws<PagamentoInvalidoException>(() =>
            pagamento.SolicitarEstorno("estorno-1", Guid.NewGuid(), 10m, "BRL", "x", _agora, _intervalo));
    }

    [Fact]
    public void EstornoAprovadoNoProvedor_Estorna()
    {
        var pagamento = ComEstornoPedido();

        var evento = pagamento.RegistrarEstorno(new EstornoNoProvedor("r-1", StatusEstornoNoProvedor.Aprovado, "approved"), _agora, _intervalo);

        Assert.Equal(EventoPagamento.Estornado, evento);
        Assert.Equal(StatusPagamento.Estornado, pagamento.Status);
        Assert.Equal("r-1", pagamento.ReferenciaEstorno);
    }

    [Fact]
    public void EstornoEmProcessamento_PublicaPendenteUmaVezEConfirmaPelaConsulta()
    {
        var pagamento = ComEstornoPedido();

        var primeira = pagamento.RegistrarEstorno(new EstornoNoProvedor("r-1", StatusEstornoNoProvedor.Pendente, "in_process"), _agora, _intervalo);
        var segunda = pagamento.RegistrarEstorno(new EstornoNoProvedor("r-1", StatusEstornoNoProvedor.Pendente, "in_process"), _agora, _intervalo);
        var confirmado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Estornado, "refunded"), _agora, _intervalo);

        Assert.Equal(EventoPagamento.EstornoPendente, primeira);
        Assert.Null(segunda);
        Assert.False(pagamento.PrecisaPedirEstorno);
        Assert.Equal(EventoPagamento.Estornado, confirmado.Evento);
    }

    [Fact]
    public void EstornoRecusadoNoProvedor_Falha()
    {
        var pagamento = ComEstornoPedido();

        var evento = pagamento.RegistrarEstorno(new EstornoNoProvedor("r-1", StatusEstornoNoProvedor.Recusado, "rejected"), _agora, _intervalo);

        Assert.Equal(EventoPagamento.EstornoFalhou, evento);
        Assert.Equal(StatusPagamento.EstornoFalhou, pagamento.Status);
    }

    [Fact]
    public void FalhaTecnicaNoEstorno_PublicaPendenteEDepoisFalhaNoLimite()
    {
        var pagamento = ComEstornoPedido();

        var eventos = Enumerable.Range(0, MaxTentativas)
            .Select(_ => pagamento.RegistrarFalhaTecnica(_agora, _intervalo, MaxTentativas))
            .ToList();

        Assert.Equal([EventoPagamento.EstornoPendente, null, EventoPagamento.EstornoFalhou], eventos);
        Assert.Equal(StatusPagamento.EstornoFalhou, pagamento.Status);
    }

    [Fact]
    public void EstornoNoProvedorSemPedidoDaSaga_ForaDoFluxo()
    {
        var pagamento = Aprovado();

        var resultado = pagamento.Reconciliar(NoProvedor(pagamento, StatusNoProvedor.Estornado, "refunded"), _agora, _intervalo);

        Assert.Equal(ResultadoReconciliacao.ForaDoFluxo, resultado.Resultado);
        Assert.Equal(StatusPagamento.Aprovado, pagamento.Status);
    }
}
