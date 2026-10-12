using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.UnitTests.Dominio;

public class OrcamentoTests
{
    private static readonly DateTimeOffset _agora = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _validade = TimeSpan.FromHours(24);
    private static readonly Guid _clienteId = Guid.NewGuid();

    private static ItemPrecificado Item(decimal? preco = 100m, int quantidade = 1, TipoItemOrcamento tipo = TipoItemOrcamento.Peca)
        => new(Guid.NewGuid(), tipo, "Pastilha de freio", quantidade, preco);

    private static Orcamento Criar(params ItemPrecificado[] itens)
        => Orcamento.Criar(Guid.NewGuid(), _clienteId, Guid.NewGuid(), Guid.NewGuid(), "quote-1", "BRL", _agora.AddMinutes(-5),
            itens.Length > 0 ? itens : [Item()], _agora, _validade);

    [Fact]
    public void Criar_CalculaTotalPeloSnapshotEDefineValidade()
    {
        var orcamento = Criar(Item(120.50m, 2), Item(80m, 1, TipoItemOrcamento.Servico));

        var versao = orcamento.VersaoVigente;
        Assert.Equal(1, versao.Numero);
        Assert.Equal(321m, versao.ValorTotal);
        Assert.Equal("BRL", versao.Moeda);
        Assert.Equal(_agora + _validade, versao.ExpiraEm);
        Assert.Equal(StatusVersaoOrcamento.AguardandoDecisao, versao.Status);
        Assert.Equal([1, 2], versao.Itens.Select(i => i.Sequencia));
    }

    public static TheoryData<string, ItemPrecificado[]> PedidosInvalidos() => new()
    {
        { "sem itens", [] },
        { "item sem preço", [Item(preco: null)] },
        { "preço zero", [Item(0m)] },
        { "preço negativo", [Item(-1m)] },
        { "três casas decimais", [Item(10.123m)] },
        { "quantidade zero", [Item(quantidade: 0)] }
    };

    [Theory]
    [MemberData(nameof(PedidosInvalidos))]
    public void Criar_PedidoInvalido_LancaOrcamentoInvalido(string caso, ItemPrecificado[] itens)
    {
        Assert.NotNull(caso);
        Assert.Throws<OrcamentoInvalidoException>(() => Orcamento.Criar(
            Guid.NewGuid(), _clienteId, Guid.NewGuid(), Guid.NewGuid(), "quote-1", "BRL", _agora, itens, _agora, _validade));
    }

    [Fact]
    public void Criar_MoedaDiferenteDeBrl_LancaOrcamentoInvalido()
    {
        Assert.Throws<OrcamentoInvalidoException>(() => Orcamento.Criar(
            Guid.NewGuid(), _clienteId, Guid.NewGuid(), Guid.NewGuid(), "quote-1", "USD", _agora, [Item()], _agora, _validade));
    }

    [Fact]
    public void Aprovar_VersaoVigenteDentroDaValidade_RegistraDecisaoEHistorico()
    {
        var orcamento = Criar();

        var tentativa = orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora.AddHours(1));

        Assert.Equal(StatusTentativaDecisao.Registrada, tentativa.Status);
        Assert.Equal(StatusVersaoOrcamento.Aprovada, orcamento.VersaoVigente.Status);
        var decisao = Assert.Single(orcamento.Decisoes);
        Assert.Equal((1, ResultadoDecisao.Aprovado, _clienteId), (decisao.Versao, decisao.Resultado, decisao.ClienteId));
        Assert.NotNull(orcamento.VersaoAprovada(1));
    }

    [Fact]
    public void Recusar_GuardaMotivoENaoLiberaCobranca()
    {
        var orcamento = Criar();

        var tentativa = orcamento.Decidir(1, ResultadoDecisao.Recusado, _clienteId, "  Achei caro  ", _agora);

        Assert.Equal(StatusTentativaDecisao.Registrada, tentativa.Status);
        Assert.Equal(StatusVersaoOrcamento.Recusada, orcamento.VersaoVigente.Status);
        Assert.Equal("Achei caro", Assert.Single(orcamento.Decisoes).Motivo);
        Assert.Null(orcamento.VersaoAprovada(1));
    }

    [Fact]
    public void MesmaDecisaoRepetida_DevolveJaRegistradaSemNovoHistorico()
    {
        var orcamento = Criar();
        orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora);

        var repetida = orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora.AddMinutes(1));

        Assert.Equal(StatusTentativaDecisao.JaRegistrada, repetida.Status);
        Assert.Single(orcamento.Decisoes);
    }

    [Fact]
    public void DecisaoOposta_DepoisDeDecidido_EConflitante()
    {
        var orcamento = Criar();
        orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora);

        var oposta = orcamento.Decidir(1, ResultadoDecisao.Recusado, _clienteId, null, _agora);

        Assert.Equal(StatusTentativaDecisao.DecisaoConflitante, oposta.Status);
        Assert.Equal(StatusVersaoOrcamento.Aprovada, orcamento.VersaoVigente.Status);
        Assert.Single(orcamento.Decisoes);
    }

    [Fact]
    public void DecisaoDepoisDaValidade_ExpiraAVersaoUmaVez()
    {
        var orcamento = Criar();

        var primeira = orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora + _validade);
        var segunda = orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora + _validade);

        Assert.Equal((StatusTentativaDecisao.Expirada, true), (primeira.Status, primeira.ExpirouAgora));
        Assert.Equal((StatusTentativaDecisao.Expirada, false), (segunda.Status, segunda.ExpirouAgora));
        Assert.Equal(StatusVersaoOrcamento.Expirada, orcamento.VersaoVigente.Status);
        Assert.Empty(orcamento.Decisoes);
        Assert.Null(orcamento.VersaoAprovada(1));
    }

    [Fact]
    public void NovaVersao_SubstituiAPendenteEDecisaoNaAntigaEDesatualizada()
    {
        var orcamento = Criar();

        var v2 = orcamento.NovaVersao("quote-2", "BRL", _agora, [Item(150m)], _agora.AddHours(1), _validade);
        var decisaoAntiga = orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora.AddHours(2));

        Assert.Equal(2, v2.Numero);
        Assert.Equal(StatusVersaoOrcamento.Substituida, orcamento.Versoes.Single(v => v.Numero == 1).Status);
        Assert.Equal(StatusTentativaDecisao.VersaoDesatualizada, decisaoAntiga.Status);
        Assert.Equal(StatusVersaoOrcamento.AguardandoDecisao, v2.Status);
        Assert.Empty(orcamento.Decisoes);
    }

    [Fact]
    public void Decidir_VersaoQueNaoExiste_EInexistente()
    {
        var orcamento = Criar();

        Assert.Equal(StatusTentativaDecisao.VersaoInexistente, orcamento.Decidir(7, ResultadoDecisao.Aprovado, _clienteId, null, _agora).Status);
    }

    [Fact]
    public void NovaVersao_DepoisDaDecisao_ERecusada()
    {
        var orcamento = Criar();
        orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora);

        Assert.Throws<OrcamentoInvalidoException>(() => orcamento.NovaVersao("quote-2", "BRL", _agora, [Item()], _agora, _validade));
        Assert.Single(orcamento.Versoes);
    }

    [Fact]
    public void NovaVersao_Invalida_NaoSubstituiAVigente()
    {
        var orcamento = Criar();

        Assert.Throws<OrcamentoInvalidoException>(() => orcamento.NovaVersao("quote-2", "BRL", _agora, [Item(0m)], _agora, _validade));
        Assert.Equal(StatusVersaoOrcamento.AguardandoDecisao, orcamento.VersaoVigente.Status);
        Assert.Single(orcamento.Versoes);
    }

    [Fact]
    public void Decidir_OutroCliente_Lanca()
    {
        var orcamento = Criar();

        Assert.Throws<InvalidOperationException>(() => orcamento.Decidir(1, ResultadoDecisao.Aprovado, Guid.NewGuid(), null, _agora));
        Assert.Equal(StatusVersaoOrcamento.AguardandoDecisao, orcamento.VersaoVigente.Status);
    }

    [Fact]
    public void ExpirarSeVencido_SoExpiraVersaoPendenteVencida()
    {
        var orcamento = Criar();

        Assert.Null(orcamento.ExpirarSeVencido(_agora.AddHours(1)));
        Assert.NotNull(orcamento.ExpirarSeVencido(_agora + _validade));
        Assert.Null(orcamento.ExpirarSeVencido(_agora + _validade));
        Assert.Equal(StatusVersaoOrcamento.Expirada, orcamento.VersaoVigente.Status);
    }

    [Fact]
    public void ExpirarSeVencido_VersaoAprovada_NaoMuda()
    {
        var orcamento = Criar();
        orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, _agora);

        Assert.Null(orcamento.ExpirarSeVencido(_agora + _validade));
        Assert.Equal(StatusVersaoOrcamento.Aprovada, orcamento.VersaoVigente.Status);
    }
}
