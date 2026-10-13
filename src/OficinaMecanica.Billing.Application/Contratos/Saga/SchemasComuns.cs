namespace OficinaMecanica.Billing.Application.Contratos.Saga;

// Bases que espelham os schemas reutilizados da spec (components/schemas): cada campo é declarado uma vez.
// Cada messageType continua com seu próprio record, pois tem canal próprio no AsyncAPI (ADR-018).

// CommandEnvelope = Envelope + idempotencyKey obrigatória.
public abstract record ComandoSaga : MensagemSaga
{
    protected override void ValidarPayload(List<string> erros)
    {
        if (IdempotencyKey is null)
            erros.Add("idempotencyKey é obrigatória em comandos.");
    }
}

// Rejection = Envelope + Reason.
public abstract record Rejection : MensagemSaga
{
    public required string Reason { get; init; }

    protected override void ValidarPayload(List<string> erros) => ExigirTexto(erros, Reason, "reason");
}

// Envelope + QuoteRef (quoteId + quoteVersion).
public abstract record MensagemOrcamento : MensagemSaga
{
    public required Guid QuoteId { get; init; }
    public required int QuoteVersion { get; init; }

    protected override void ValidarPayload(List<string> erros) => ExigirMinimo(erros, QuoteVersion, 1, "quoteVersion");
}

// PaymentRef = Envelope + paymentId + providerReference opcional.
public abstract record PaymentRef : MensagemSaga
{
    public required Guid PaymentId { get; init; }
    public string? ProviderReference { get; init; }
}

// RefundResult = Envelope + paymentId + providerRefundReference opcional.
public abstract record RefundResult : MensagemSaga
{
    public required Guid PaymentId { get; init; }
    public string? ProviderRefundReference { get; init; }
}

// QuoteDecision = Envelope + QuoteRef + decidedAtUtc.
public abstract record QuoteDecision : MensagemOrcamento
{
    public required DateTimeOffset DecidedAtUtc { get; init; }
}
