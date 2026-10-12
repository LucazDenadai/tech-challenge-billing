using System.ComponentModel.DataAnnotations;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Orcamentos;

// Validade de cada versão, a partir da publicação (ADR-017: 24 horas por padrão).
public sealed record ConfiguracaoOrcamento(TimeSpan Validade);

public sealed record AprovarOrcamentoRequest
{
    /// <summary>Versão apresentada ao cliente. A aprovação vale só para ela.</summary>
    [Range(1, int.MaxValue)]
    public int Versao { get; init; }
}

public sealed record RecusarOrcamentoRequest
{
    /// <summary>Versão apresentada ao cliente.</summary>
    [Range(1, int.MaxValue)]
    public int Versao { get; init; }

    /// <summary>Motivo opcional da recusa.</summary>
    [MaxLength(500)]
    public string? Motivo { get; init; }
}

public sealed record ItemOrcamentoResponse(Guid ItemId, TipoItemOrcamento Tipo, string Descricao, int Quantidade, decimal PrecoUnitario, decimal Subtotal);

public sealed record VersaoOrcamentoResponse(
    int Versao, StatusVersaoOrcamento Status, decimal ValorTotal, string Moeda, DateTimeOffset PrecoSnapshotEm,
    DateTimeOffset ExpiraEm, DateTimeOffset? EncerradaEm, IReadOnlyList<ItemOrcamentoResponse> Itens);

public sealed record DecisaoOrcamentoResponse(int Versao, ResultadoDecisao Resultado, DateTimeOffset DecididaEm, string? Motivo);

public sealed record OrcamentoResponse(
    Guid QuoteId, Guid OsId, Guid FilialId, VersaoOrcamentoResponse VersaoVigente,
    IReadOnlyList<VersaoOrcamentoResponse> Versoes, IReadOnlyList<DecisaoOrcamentoResponse> Decisoes)
{
    public static OrcamentoResponse De(Orcamento o) => new(
        o.Id, o.OsId, o.FilialId, Versao(o.VersaoVigente),
        o.Versoes.OrderBy(v => v.Numero).Select(Versao).ToList(),
        o.Decisoes.OrderBy(d => d.DecididaEm).Select(d => new DecisaoOrcamentoResponse(d.Versao, d.Resultado, d.DecididaEm, d.Motivo)).ToList());

    private static VersaoOrcamentoResponse Versao(VersaoOrcamento v) => new(
        v.Numero, v.Status, v.ValorTotal, v.Moeda, v.PrecoSnapshotEm, v.ExpiraEm, v.EncerradaEm,
        v.Itens.OrderBy(i => i.Sequencia)
            .Select(i => new ItemOrcamentoResponse(i.ItemId, i.Tipo, i.Descricao, i.Quantidade, i.PrecoUnitario, i.Subtotal))
            .ToList());
}
