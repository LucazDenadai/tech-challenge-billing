using System.Text.Json;
using System.Text.Json.Serialization;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Application.Contratos.Saga;

// De onde vem o envelope de um evento: correlação, OS e filial da Saga; causationId = messageId do comando
// que levou ao evento (ADR-017). Eventos da API e da expiração não têm comando de origem.
public readonly record struct OrigemEvento(Guid CorrelationId, Guid? CausationId, Guid OsId, Guid FilialId)
{
    public static OrigemEvento De(MensagemSaga comando) => new(comando.CorrelationId, comando.MessageId, comando.OsId, comando.FilialId);

    public static OrigemEvento De(Orcamento orcamento) => new(orcamento.CorrelationId, null, orcamento.OsId, orcamento.FilialId);
}

// Monta e serializa os eventos que Billing publica.
public static class RespostaSaga
{
    private static readonly JsonSerializerOptions _opcoes = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static QuoteReady Pronto(OrigemEvento o, Guid quoteId, VersaoOrcamento versao) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<QuoteReady>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        QuoteId = quoteId, QuoteVersion = versao.Numero, Amount = versao.ValorTotal, Currency = versao.Moeda, ExpiresAtUtc = versao.ExpiraEm
    };

    public static QuoteRejected Recusado(OrigemEvento o, string motivo) => new()
    {
        MessageId = Guid.NewGuid(), MessageType = Tipo<QuoteRejected>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
        OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
        Reason = motivo
    };

    public static QuoteDecision Decidido(Orcamento orcamento, VersaoOrcamento versao, DateTimeOffset decididoEm)
    {
        var o = OrigemEvento.De(orcamento);
        return versao.Status switch
        {
            StatusVersaoOrcamento.Aprovada => new QuoteApproved
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<QuoteApproved>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, OsId = o.OsId, FilialId = o.FilialId,
                QuoteId = orcamento.Id, QuoteVersion = versao.Numero, DecidedAtUtc = decididoEm
            },
            StatusVersaoOrcamento.Recusada => new QuoteDeclined
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<QuoteDeclined>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, OsId = o.OsId, FilialId = o.FilialId,
                QuoteId = orcamento.Id, QuoteVersion = versao.Numero, DecidedAtUtc = decididoEm
            },
            var outro => throw new InvalidOperationException($"Versão no status '{outro}' não tem decisão.")
        };
    }

    public static QuoteExpired Expirado(Orcamento orcamento, VersaoOrcamento versao)
    {
        var o = OrigemEvento.De(orcamento);
        return new QuoteExpired
        {
            MessageId = Guid.NewGuid(), MessageType = Tipo<QuoteExpired>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
            OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, OsId = o.OsId, FilialId = o.FilialId,
            QuoteId = orcamento.Id, QuoteVersion = versao.Numero
        };
    }

    public static string Serializar(MensagemSaga mensagem) => JsonSerializer.Serialize(mensagem, mensagem.GetType(), _opcoes);

    private static string Tipo<T>() => CatalogoCanaisBilling.Publicado(typeof(T)).MessageType;
}
