using System.Text.Json;
using System.Text.Json.Serialization;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.Contratos.Saga;

// De onde vem o envelope de um evento: correlação, OS e filial da Saga; causationId = messageId do comando
// que levou ao evento (ADR-017). Eventos da API e da expiração não têm comando de origem.
public readonly record struct OrigemEvento(Guid CorrelationId, Guid? CausationId, Guid OsId, Guid FilialId)
{
    public static OrigemEvento De(MensagemSaga comando) => new(comando.CorrelationId, comando.MessageId, comando.OsId, comando.FilialId);

    public static OrigemEvento De(Orcamento orcamento) => new(orcamento.CorrelationId, null, orcamento.OsId, orcamento.FilialId);

    // Resposta ao comando de criação ou de estorno leva o messageId dele; aprovação e recusa vêm do provedor, sem comando de origem.
    public static OrigemEvento De(Pagamento pagamento, EventoPagamento evento) => new(pagamento.CorrelationId,
        evento switch
        {
            EventoPagamento.Pendente or EventoPagamento.ResultadoDesconhecido => pagamento.MensagemCriacaoId,
            EventoPagamento.EstornoPendente or EventoPagamento.Estornado or EventoPagamento.EstornoFalhou => pagamento.MensagemEstornoId,
            _ => null
        },
        pagamento.OsId, pagamento.FilialId);
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

    // Evento da Saga para uma mudança de estado do pagamento (ADR-019).
    public static MensagemSaga DoPagamento(Pagamento pagamento, EventoPagamento evento)
    {
        var o = OrigemEvento.De(pagamento, evento);
        return evento switch
        {
            EventoPagamento.Pendente => new PaymentPending
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentPending>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderReference = pagamento.ReferenciaProvedor
            },
            EventoPagamento.Aprovado => new PaymentApproved
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentApproved>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderReference = pagamento.ReferenciaProvedor,
                Amount = pagamento.Valor, Currency = pagamento.Moeda, ApprovedAtUtc = pagamento.AprovadoEm!.Value
            },
            EventoPagamento.Recusado => new PaymentDeclined
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentDeclined>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderReference = pagamento.ReferenciaProvedor,
                Reason = pagamento.MotivoRecusa ?? "Pagamento recusado no Mercado Pago."
            },
            EventoPagamento.ResultadoDesconhecido => new PaymentOutcomeUnknown
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentOutcomeUnknown>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderReference = pagamento.ReferenciaProvedor,
                Reason = "Sem resposta do Mercado Pago ao criar a cobrança; a reconciliação continua com a mesma chave idempotente."
            },
            EventoPagamento.EstornoPendente => new PaymentRefundPending
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentRefundPending>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderRefundReference = pagamento.ReferenciaEstorno
            },
            EventoPagamento.Estornado => new PaymentRefunded
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentRefunded>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderRefundReference = pagamento.ReferenciaEstorno
            },
            EventoPagamento.EstornoFalhou => new PaymentRefundFailed
            {
                MessageId = Guid.NewGuid(), MessageType = Tipo<PaymentRefundFailed>(), SchemaVersion = 1, Producer = CatalogoCanaisBilling.Produtor,
                OccurredAtUtc = DateTimeOffset.UtcNow, CorrelationId = o.CorrelationId, CausationId = o.CausationId, OsId = o.OsId, FilialId = o.FilialId,
                PaymentId = pagamento.Id, ProviderRefundReference = pagamento.ReferenciaEstorno,
                Reason = pagamento.MotivoFalhaEstorno ?? "Estorno não confirmado pelo Mercado Pago."
            },
            _ => throw new ArgumentOutOfRangeException(nameof(evento), evento, null)
        };
    }

    public static string Serializar(MensagemSaga mensagem) => JsonSerializer.Serialize(mensagem, mensagem.GetType(), _opcoes);

    private static string Tipo<T>() => CatalogoCanaisBilling.Publicado(typeof(T)).MessageType;
}
