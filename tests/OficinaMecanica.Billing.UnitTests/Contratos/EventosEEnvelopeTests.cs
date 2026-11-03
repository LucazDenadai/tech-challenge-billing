using System.Text;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.UnitTests.Contratos;

// Billing não lê os eventos que publica, então os validadores deles só rodam aqui: cada evento montado pela
// RespostaSaga passa no próprio validador, e um evento malformado é recusado com o campo certo (ADR-018).
// Também cobre as recusas do leitor e do envelope que os exemplos da spec não exercitam.
public class EventosEEnvelopeTests
{
    private static readonly DateTimeOffset _agora = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly CanalConsumido _canal = CatalogoCanaisBilling.Consumidos[0];

    private static Orcamento NovoOrcamento()
        => Orcamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "quote-1", "BRL", _agora.AddMinutes(-5),
            [new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Peca, "Pastilha de freio", 1, 100m)], _agora, TimeSpan.FromHours(24));

    private static Pagamento Aprovado()
    {
        var pagamento = Pagamento.Criar(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            125.90m, "BRL", "pagamento-1", Guid.NewGuid(), "cliente@exemplo.com", _agora, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2));
        pagamento.Reconciliar(new PagamentoNoProvedor("123", pagamento.Id.ToString(), pagamento.Valor, "BRL", StatusNoProvedor.Aprovado, "approved",
            _agora.AddMinutes(5), "00020126pix", "https://mp.exemplo/pix"), _agora, TimeSpan.FromMinutes(2));
        return pagamento;
    }

    public static TheoryData<EventoPagamento> EventosDePagamento() => new(Enum.GetValues<EventoPagamento>());

    [Theory]
    [MemberData(nameof(EventosDePagamento))]
    public void EventoDePagamento_PassaNoProprioValidador(EventoPagamento evento)
    {
        var mensagem = RespostaSaga.DoPagamento(Aprovado(), evento);

        Assert.Empty(mensagem.Validar());
        Assert.Equal(CatalogoCanaisBilling.Publicado(mensagem.GetType()).MessageType, mensagem.MessageType);
    }

    [Fact]
    public void EventoDePagamentoDesconhecido_LancaExcecao()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RespostaSaga.DoPagamento(Aprovado(), (EventoPagamento)99));

    [Fact]
    public void EventosDePagamentoMalformados_RecusadosComOCampo()
    {
        var pagamento = Aprovado();

        Assert.Contains(((PaymentDeclined)RespostaSaga.DoPagamento(pagamento, EventoPagamento.Recusado) with { Reason = "" }).Validar(), e => e.Contains("reason"));
        Assert.Contains(((PaymentOutcomeUnknown)RespostaSaga.DoPagamento(pagamento, EventoPagamento.ResultadoDesconhecido) with { Reason = "" }).Validar(), e => e.Contains("reason"));
        Assert.Contains(((PaymentRefundFailed)RespostaSaga.DoPagamento(pagamento, EventoPagamento.EstornoFalhou) with { Reason = "" }).Validar(), e => e.Contains("reason"));
        var aprovado = (PaymentApproved)RespostaSaga.DoPagamento(pagamento, EventoPagamento.Aprovado);
        Assert.Contains((aprovado with { Currency = "brl" }).Validar(), e => e.Contains("currency"));
        Assert.Contains((aprovado with { Amount = 0m }).Validar(), e => e.Contains("amount"));
    }

    [Fact]
    public void EventosDeOrcamento_PassamNoProprioValidadorEMalformadosSaoRecusados()
    {
        var orcamento = NovoOrcamento();
        var versao = orcamento.VersaoVigente;
        var origem = OrigemEvento.De(orcamento);

        var pronto = RespostaSaga.Pronto(origem, orcamento.Id, versao);
        var recusado = RespostaSaga.Recusado(origem, "Item sem preço.");
        var expirado = RespostaSaga.Expirado(orcamento, versao);

        Assert.Empty(pronto.Validar());
        Assert.Empty(recusado.Validar());
        Assert.Empty(expirado.Validar());
        Assert.Contains((pronto with { Amount = -1m }).Validar(), e => e.Contains("amount"));
        Assert.Contains((recusado with { Reason = "" }).Validar(), e => e.Contains("reason"));
        Assert.Contains((expirado with { QuoteVersion = 0 }).Validar(), e => e.Contains("quoteVersion"));
    }

    [Fact]
    public void Decisao_DeVersaoSemDecisao_LancaExcecao()
    {
        var orcamento = NovoOrcamento();

        var ex = Assert.Throws<InvalidOperationException>(() => RespostaSaga.Decidido(orcamento, orcamento.VersaoVigente, _agora));

        Assert.Contains("AguardandoDecisao", ex.Message);
    }

    [Fact]
    public void Envelope_SchemaVersionZeroEProdutorDesconhecido_DoisErros()
    {
        var evento = RespostaSaga.Recusado(OrigemEvento.De(NovoOrcamento()), "motivo") with { SchemaVersion = 0, Producer = "caixa" };

        var erros = evento.Validar();

        Assert.Contains(erros, e => e.Contains("schemaVersion"));
        Assert.Contains(erros, e => e.Contains("producer 'caixa'"));
    }

    [Fact]
    public void PedidoDeOrcamento_SemCliente_Recusado()
    {
        var canal = CatalogoCanaisBilling.ObterConsumido("saga-os.quote-requested.v1")!;
        var pedido = (QuoteRequested)LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(MensagensExemplo.Criar(canal))).Mensagem!;

        Assert.Contains((pedido with { ClienteId = Guid.Empty }).Validar(), e => e.Contains("clienteId"));
    }

    [Fact]
    public void Ler_CorpoQueNaoEJson_RejeitadoSemIdentificadores()
    {
        var leitura = LeitorMensagemSaga.Ler(_canal, Encoding.UTF8.GetBytes("{ isto não é json"));

        Assert.False(leitura.Valida);
        Assert.Equal("Corpo não é um JSON válido.", leitura.Motivo);
        Assert.Null(leitura.MessageId);
    }

    [Fact]
    public void Ler_JsonQueNaoEObjeto_Rejeitado()
    {
        var leitura = LeitorMensagemSaga.Ler(_canal, Encoding.UTF8.GetBytes("[1, 2]"));

        Assert.False(leitura.Valida);
        Assert.Equal("Corpo não é um objeto JSON.", leitura.Motivo);
    }

    [Fact]
    public void Publicado_TipoQueBillingNaoPublica_LancaExcecao()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogoCanaisBilling.Publicado(typeof(QuoteRequested)));

        Assert.Contains(nameof(QuoteRequested), ex.Message);
    }
}
