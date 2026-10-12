using System.Text.RegularExpressions;

namespace OficinaMecanica.Billing.Application.Contratos.Saga;

// Envelope comum de toda mensagem da Saga (ADR-017). Campos e restrições seguem o schema
// `Envelope` de tech-challenge-docs/contratos/asyncapi-saga-os.yaml (ADR-018); não há pacote compartilhado.
// Propriedades `required` e não anuláveis são exigidas na desserialização; as demais restrições do schema
// (mínimos, padrões, enums) são verificadas em Validar().
public abstract record MensagemSaga
{
    private static readonly string[] _produtores = ["os", "billing", "operacoes"];

    public required Guid MessageId { get; init; }
    public required string MessageType { get; init; }
    public required int SchemaVersion { get; init; }
    public required string Producer { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public required Guid OsId { get; init; }
    public required Guid FilialId { get; init; }
    public string? IdempotencyKey { get; init; }

    public IReadOnlyList<string> Validar()
    {
        var erros = new List<string>();

        if (SchemaVersion < 1)
            erros.Add("schemaVersion deve ser maior ou igual a 1.");
        if (!_produtores.Contains(Producer))
            erros.Add($"producer '{Producer}' não pertence ao contrato.");
        if (IdempotencyKey is { Length: 0 })
            erros.Add("idempotencyKey não pode ser vazio.");

        ValidarPayload(erros);
        return erros;
    }

    protected virtual void ValidarPayload(List<string> erros) { }

    protected static void ExigirTexto(List<string> erros, string? valor, string campo)
    {
        if (string.IsNullOrEmpty(valor))
            erros.Add($"{campo} não pode ser vazio.");
    }

    protected static void ExigirMoeda(List<string> erros, string valor, string campo = "currency")
    {
        if (!Regex.IsMatch(valor, "^[A-Z]{3}$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            erros.Add($"{campo} deve ter três letras maiúsculas (ISO 4217).");
    }

    // Money: número positivo com no máximo duas casas decimais.
    protected static void ExigirValor(List<string> erros, decimal valor, string campo)
    {
        if (valor <= 0)
            erros.Add($"{campo} deve ser maior que zero.");
        else if (decimal.Round(valor, 2) != valor)
            erros.Add($"{campo} deve ter no máximo duas casas decimais.");
    }

    protected static void ExigirMinimo(List<string> erros, int valor, int minimo, string campo)
    {
        if (valor < minimo)
            erros.Add($"{campo} deve ser maior ou igual a {minimo}.");
    }
}
