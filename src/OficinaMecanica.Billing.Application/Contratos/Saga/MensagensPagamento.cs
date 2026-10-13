using System.Text.RegularExpressions;

namespace OficinaMecanica.Billing.Application.Contratos.Saga;

// Mensagens de pagamento e estorno no asyncapi-saga-os.yaml (ADR-018, ADR-019).

// Comandos do OS consumidos por Billing.

// payerEmail é opcional na spec (campo novo compatível); sem ele o caso de uso recusa o comando e ele vai para a DLQ.
public sealed record PaymentCreationRequested : ComandoSaga
{
    private static readonly Regex _email = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public required Guid QuoteId { get; init; }
    public required int QuoteVersion { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? PayerEmail { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        base.ValidarPayload(erros);
        ExigirMinimo(erros, QuoteVersion, 1, "quoteVersion");
        ExigirValor(erros, Amount, "amount");
        ExigirMoeda(erros, Currency);
        if (PayerEmail is not null && !_email.IsMatch(PayerEmail))
            erros.Add("payerEmail não é um e-mail válido.");
    }
}

public sealed record PaymentRefundRequested : ComandoSaga
{
    public required Guid PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string Reason { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        base.ValidarPayload(erros);
        ExigirValor(erros, Amount, "amount");
        ExigirMoeda(erros, Currency);
        ExigirTexto(erros, Reason, "reason");
    }
}

// Eventos publicados por Billing para o OS.

public sealed record PaymentPending : PaymentRef;

public sealed record PaymentApproved : PaymentRef
{
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset ApprovedAtUtc { get; init; }

    protected override void ValidarPayload(List<string> erros)
    {
        ExigirValor(erros, Amount, "amount");
        ExigirMoeda(erros, Currency);
    }
}

public sealed record PaymentDeclined : PaymentRef
{
    public required string Reason { get; init; }

    protected override void ValidarPayload(List<string> erros) => ExigirTexto(erros, Reason, "reason");
}

public sealed record PaymentOutcomeUnknown : PaymentRef
{
    public required string Reason { get; init; }

    protected override void ValidarPayload(List<string> erros) => ExigirTexto(erros, Reason, "reason");
}

public sealed record PaymentRefunded : RefundResult;

public sealed record PaymentRefundPending : RefundResult;

public sealed record PaymentRefundFailed : RefundResult
{
    public required string Reason { get; init; }

    protected override void ValidarPayload(List<string> erros) => ExigirTexto(erros, Reason, "reason");
}
