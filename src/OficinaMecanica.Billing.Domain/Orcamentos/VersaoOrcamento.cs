using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Billing.Domain.Comum;

namespace OficinaMecanica.Billing.Domain.Orcamentos;

// Uma versão apresentada ao cliente. Numero é o quoteVersion dos contratos; a decisão vale para uma versão exata.
public class VersaoOrcamento : EntityBase
{
    public Guid OrcamentoId { get; private set; }
    public int Numero { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string Moeda { get; private set; } = string.Empty;
    public decimal ValorTotal { get; private set; }
    public DateTimeOffset PrecoSnapshotEm { get; private set; }
    public DateTimeOffset ExpiraEm { get; private set; }
    public StatusVersaoOrcamento Status { get; private set; } = StatusVersaoOrcamento.AguardandoDecisao;
    public DateTimeOffset? EncerradaEm { get; private set; }

    private readonly List<ItemOrcamento> _itens = new();
    public IReadOnlyCollection<ItemOrcamento> Itens => _itens.AsReadOnly();

    [ExcludeFromCodeCoverage]
    protected VersaoOrcamento() { }

    internal VersaoOrcamento(Guid orcamentoId, int numero, string idempotencyKey, string moeda, DateTimeOffset precoSnapshotEm,
        IReadOnlyList<ItemPrecificado> itens, DateTimeOffset publicadaEm, TimeSpan validade)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(idempotencyKey));
        if (validade <= TimeSpan.Zero)
            throw new ArgumentException("A validade do orçamento deve ser positiva.", nameof(validade));
        if (moeda != Dinheiro.Moeda)
            throw new OrcamentoInvalidoException($"Moeda '{moeda}' não aceita; o orçamento é em {Dinheiro.Moeda}.");
        if (itens.Count == 0)
            throw new OrcamentoInvalidoException("O orçamento precisa de ao menos um item.");

        OrcamentoId = orcamentoId;
        Numero = numero;
        IdempotencyKey = idempotencyKey;
        Moeda = moeda;
        PrecoSnapshotEm = precoSnapshotEm;
        ExpiraEm = publicadaEm + validade;

        for (var i = 0; i < itens.Count; i++)
            _itens.Add(new ItemOrcamento(Id, i + 1, itens[i]));

        ValorTotal = _itens.Sum(i => i.Subtotal);
    }

    public bool AguardandoDecisao => Status == StatusVersaoOrcamento.AguardandoDecisao;

    public bool Vencida(DateTimeOffset agora) => agora >= ExpiraEm;

    internal void Encerrar(StatusVersaoOrcamento status, DateTimeOffset quando)
    {
        if (!AguardandoDecisao)
            throw new InvalidOperationException($"Versão {Numero} no status '{Status}' não pode mudar para '{status}'.");

        Status = status;
        EncerradaEm = quando;
        MarcarAtualizado();
    }
}

public class ItemOrcamento
{
    public Guid VersaoId { get; private set; }
    public int Sequencia { get; private set; }
    public Guid ItemId { get; private set; }
    public TipoItemOrcamento Tipo { get; private set; }
    public string Descricao { get; private set; } = string.Empty;
    public int Quantidade { get; private set; }
    public decimal PrecoUnitario { get; private set; }

    public decimal Subtotal => Quantidade * PrecoUnitario;

    [ExcludeFromCodeCoverage]
    protected ItemOrcamento() { }

    internal ItemOrcamento(Guid versaoId, int sequencia, ItemPrecificado item)
    {
        if (string.IsNullOrWhiteSpace(item.Descricao))
            throw new OrcamentoInvalidoException($"Item {sequencia} sem descrição.");
        if (item.Quantidade < 1)
            throw new OrcamentoInvalidoException($"Item {sequencia} com quantidade menor que 1.");
        if (item.PrecoUnitario is not { } preco)
            throw new OrcamentoInvalidoException($"Item {sequencia} sem preço.");

        VersaoId = versaoId;
        Sequencia = sequencia;
        ItemId = item.ItemId;
        Tipo = item.Tipo;
        Descricao = item.Descricao;
        Quantidade = item.Quantidade;
        PrecoUnitario = Dinheiro.Validar(preco, $"Item {sequencia}");
    }
}
