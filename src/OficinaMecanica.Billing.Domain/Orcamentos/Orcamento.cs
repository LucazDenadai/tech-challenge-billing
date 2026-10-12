using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Billing.Domain.Comum;

namespace OficinaMecanica.Billing.Domain.Orcamentos;

// Orçamento de uma OS (CARD-39a). O Id é o quoteId dos contratos; um orçamento por OS, com versões.
// ClienteId vem do QuoteRequested e identifica o único cliente que pode decidir.
public class Orcamento : EntityBase
{
    public Guid OsId { get; private set; }
    public Guid ClienteId { get; private set; }
    public Guid FilialId { get; private set; }
    public Guid CorrelationId { get; private set; }

    private readonly List<VersaoOrcamento> _versoes = new();
    public IReadOnlyCollection<VersaoOrcamento> Versoes => _versoes.AsReadOnly();

    private readonly List<DecisaoOrcamento> _decisoes = new();
    public IReadOnlyCollection<DecisaoOrcamento> Decisoes => _decisoes.AsReadOnly();

    [ExcludeFromCodeCoverage]
    protected Orcamento() { }

    private Orcamento(Guid osId, Guid clienteId, Guid filialId, Guid correlationId)
    {
        if (osId == Guid.Empty)
            throw new ArgumentException("A OS do orçamento é obrigatória.", nameof(osId));
        if (clienteId == Guid.Empty)
            throw new ArgumentException("O cliente do orçamento é obrigatório.", nameof(clienteId));
        if (filialId == Guid.Empty)
            throw new ArgumentException("A filial do orçamento é obrigatória.", nameof(filialId));

        OsId = osId;
        ClienteId = clienteId;
        FilialId = filialId;
        CorrelationId = correlationId;
    }

    // Versão mais recente: a única que aceita decisão.
    public VersaoOrcamento VersaoVigente => _versoes.MaxBy(v => v.Numero)
        ?? throw new InvalidOperationException("Orçamento sem versão.");

    public static Orcamento Criar(Guid osId, Guid clienteId, Guid filialId, Guid correlationId, string idempotencyKey,
        string moeda, DateTimeOffset precoSnapshotEm, IReadOnlyList<ItemPrecificado> itens, DateTimeOffset agora, TimeSpan validade)
    {
        var orcamento = new Orcamento(osId, clienteId, filialId, correlationId);
        orcamento._versoes.Add(new VersaoOrcamento(orcamento.Id, 1, idempotencyKey, moeda, precoSnapshotEm, itens, agora, validade));
        return orcamento;
    }

    public VersaoOrcamento? VersaoPorIdempotencyKey(string idempotencyKey)
        => _versoes.FirstOrDefault(v => v.IdempotencyKey == idempotencyKey);

    // Nova versão só enquanto a vigente aguarda decisão; ela passa a Substituida.
    // Depois de uma decisão ou expiração, revisão fica fora do MVP (ADR-017).
    public VersaoOrcamento NovaVersao(string idempotencyKey, string moeda, DateTimeOffset precoSnapshotEm,
        IReadOnlyList<ItemPrecificado> itens, DateTimeOffset agora, TimeSpan validade)
    {
        var vigente = VersaoVigente;
        if (!vigente.AguardandoDecisao || vigente.Vencida(agora))
            throw new OrcamentoInvalidoException(
                $"O orçamento da OS já foi encerrado na versão {vigente.Numero}; revisão após a decisão não faz parte do MVP.");

        // Valida antes de mudar a vigente: pedido inválido não substitui nada.
        var nova = new VersaoOrcamento(Id, vigente.Numero + 1, idempotencyKey, moeda, precoSnapshotEm, itens, agora, validade);
        vigente.Encerrar(StatusVersaoOrcamento.Substituida, agora);
        _versoes.Add(nova);
        MarcarAtualizado();
        return nova;
    }

    public TentativaDecisao Decidir(int numeroVersao, ResultadoDecisao resultado, Guid clienteId, string? motivo, DateTimeOffset agora)
    {
        if (clienteId != ClienteId)
            throw new InvalidOperationException("Só o cliente dono da OS decide o orçamento.");

        var vigente = VersaoVigente;
        if (numeroVersao != vigente.Numero)
            return new TentativaDecisao(
                _versoes.Any(v => v.Numero == numeroVersao) ? StatusTentativaDecisao.VersaoDesatualizada : StatusTentativaDecisao.VersaoInexistente,
                vigente);

        switch (vigente.Status)
        {
            case StatusVersaoOrcamento.Aprovada or StatusVersaoOrcamento.Recusada:
                var mesma = (vigente.Status == StatusVersaoOrcamento.Aprovada) == (resultado == ResultadoDecisao.Aprovado);
                return new TentativaDecisao(mesma ? StatusTentativaDecisao.JaRegistrada : StatusTentativaDecisao.DecisaoConflitante, vigente);

            case StatusVersaoOrcamento.Expirada:
                return new TentativaDecisao(StatusTentativaDecisao.Expirada, vigente);
        }

        if (vigente.Vencida(agora))
        {
            Expirar(vigente, agora);
            return new TentativaDecisao(StatusTentativaDecisao.Expirada, vigente, ExpirouAgora: true);
        }

        vigente.Encerrar(resultado == ResultadoDecisao.Aprovado ? StatusVersaoOrcamento.Aprovada : StatusVersaoOrcamento.Recusada, agora);
        _decisoes.Add(new DecisaoOrcamento(Id, vigente.Numero, resultado, clienteId, string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(), agora));
        MarcarAtualizado();
        return new TentativaDecisao(StatusTentativaDecisao.Registrada, vigente);
    }

    // Chamado pelo serviço de expiração. Retorna a versão expirada agora, ou null se não havia o que expirar.
    public VersaoOrcamento? ExpirarSeVencido(DateTimeOffset agora)
    {
        var vigente = VersaoVigente;
        if (!vigente.AguardandoDecisao || !vigente.Vencida(agora))
            return null;

        Expirar(vigente, agora);
        return vigente;
    }

    // A versão aprovada que pode gerar cobrança (CARD-39b). Pendente, recusada, expirada ou substituída não pode.
    public VersaoOrcamento? VersaoAprovada(int numeroVersao)
        => _versoes.FirstOrDefault(v => v.Numero == numeroVersao && v.Status == StatusVersaoOrcamento.Aprovada);

    private void Expirar(VersaoOrcamento versao, DateTimeOffset agora)
    {
        versao.Encerrar(StatusVersaoOrcamento.Expirada, agora);
        MarcarAtualizado();
    }
}

// Histórico da decisão do cliente: qual versão, qual resultado, quem e quando.
public class DecisaoOrcamento : EntityBase
{
    public Guid OrcamentoId { get; private set; }
    public int Versao { get; private set; }
    public ResultadoDecisao Resultado { get; private set; }
    public Guid ClienteId { get; private set; }
    public string? Motivo { get; private set; }
    public DateTimeOffset DecididaEm { get; private set; }

    [ExcludeFromCodeCoverage]
    protected DecisaoOrcamento() { }

    internal DecisaoOrcamento(Guid orcamentoId, int versao, ResultadoDecisao resultado, Guid clienteId, string? motivo, DateTimeOffset decididaEm)
    {
        OrcamentoId = orcamentoId;
        Versao = versao;
        Resultado = resultado;
        ClienteId = clienteId;
        Motivo = motivo;
        DecididaEm = decididaEm;
    }
}
