namespace OficinaMecanica.Billing.Application.Ports.In;

public sealed record OrcamentoAprovado(Guid QuoteId, int Versao, Guid OsId, Guid ClienteId, Guid FilialId, decimal Valor, string Moeda);

// Única entrada do Pagamento (CARD-39b) no Orçamento: só uma versão aprovada pode gerar cobrança.
// Pendente, recusada, expirada, substituída ou inexistente devolve null.
public interface IOrcamentoParaPagamento
{
    Task<OrcamentoAprovado?> ObterVersaoAprovadaAsync(Guid quoteId, int versao, CancellationToken ct = default);
}
