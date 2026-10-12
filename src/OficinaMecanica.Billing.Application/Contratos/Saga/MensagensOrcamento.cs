namespace OficinaMecanica.Billing.Application.Contratos.Saga;

// Mensagens do orçamento no asyncapi-saga-os.yaml (ADR-018).

// Comando do OS consumido por Billing.
//
// O leitor só recusa (DLQ) o que impede identificar o pedido: envelope, clienteId, itemId, tipo e descrição.
// Lista vazia, item sem preço, preço fora do schema Money, quantidade menor que 1 e moeda ausente ou diferente
// de BRL chegam ao caso de uso e viram QuoteRejected, como pede o ADR-017. Por isso unitPrice e currency
// são anuláveis aqui, embora a spec os exija: o OS recebe uma resposta de negócio em vez de uma mensagem perdida.
public sealed record QuoteRequested : ComandoSaga
{
    public required Guid ClienteId { get; init; }
    public required DateTimeOffset PriceSnapshotAtUtc { get; init; }
    public string? Currency { get; init; }
    public required IReadOnlyList<PricedItem> Items { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        base.ValidarPayload(erros);
        if (ClienteId == Guid.Empty)
            erros.Add("clienteId não pode ser vazio.");
        for (var i = 0; i < Items.Count; i++)
        {
            // A anotação de nulidade não vale para elementos de coleção na desserialização.
            if (Items[i] is null)
                erros.Add($"items[{i}] não pode ser nulo.");
            else
                Items[i].Validar(erros, $"items[{i}]");
        }
    }
}

public sealed record PricedItem
{
    public static readonly IReadOnlyList<string> Tipos = ["Peca", "Servico"];

    public required Guid ItemId { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public required int Quantity { get; init; }
    public decimal? UnitPrice { get; init; }

    internal void Validar(List<string> erros, string campo)
    {
        if (!Tipos.Contains(Type))
            erros.Add($"{campo}.type '{Type}' não pertence ao contrato.");
        if (string.IsNullOrEmpty(Description))
            erros.Add($"{campo}.description não pode ser vazio.");
    }
}

// Eventos publicados por Billing para o OS.

public sealed record QuoteReady : MensagemOrcamento
{
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        base.ValidarPayload(erros);
        ExigirValor(erros, Amount, "amount");
        ExigirMoeda(erros, Currency);
    }
}

public sealed record QuoteRejected : Rejection;

public sealed record QuoteApproved : QuoteDecision;

public sealed record QuoteDeclined : QuoteDecision;

public sealed record QuoteExpired : MensagemOrcamento;
