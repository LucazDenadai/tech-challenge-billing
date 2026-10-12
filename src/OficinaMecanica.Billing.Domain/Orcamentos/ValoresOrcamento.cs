namespace OficinaMecanica.Billing.Domain.Orcamentos;

// Estados de uma versão do orçamento (CARD-39a). Só AguardandoDecisao aceita decisão; os demais são terminais.
public enum StatusVersaoOrcamento
{
    AguardandoDecisao,
    Aprovada,
    Recusada,
    Expirada,
    Substituida
}

public enum TipoItemOrcamento
{
    Peca,
    Servico
}

public enum ResultadoDecisao
{
    Aprovado,
    Recusado
}

// Item do snapshot de preços do diagnóstico (ADR-017). O preço pode faltar no pedido: nesse caso o orçamento é recusado.
public sealed record ItemPrecificado(Guid ItemId, TipoItemOrcamento Tipo, string Descricao, int Quantidade, decimal? PrecoUnitario);

// Pedido de orçamento que não pode virar orçamento (item sem preço, moeda não aceita...). Vira QuoteRejected, não erro técnico.
public class OrcamentoInvalidoException(string motivo) : Exception(motivo);

public enum StatusTentativaDecisao
{
    // Decisão nova gravada: publica QuoteApproved ou QuoteDeclined.
    Registrada,
    // Mesma decisão para a mesma versão: devolve o resultado gravado, sem novo evento.
    JaRegistrada,
    // Decisão para versão que não é a vigente (409).
    VersaoDesatualizada,
    // Versão pedida não existe (404).
    VersaoInexistente,
    // Decisão oposta à já gravada (409).
    DecisaoConflitante,
    // Versão vencida (410).
    Expirada
}

// ExpirouAgora: a tentativa encontrou a versão vencida e gravou a expiração; quem chama publica QuoteExpired.
public sealed record TentativaDecisao(StatusTentativaDecisao Status, VersaoOrcamento? Versao, bool ExpirouAgora = false);
