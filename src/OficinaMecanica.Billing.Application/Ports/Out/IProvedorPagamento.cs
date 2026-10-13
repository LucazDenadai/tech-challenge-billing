using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.Ports.Out;

// Cobrança Pix no provedor (ADR-019). ChaveIdempotencia vai no X-Idempotency-Key: o mesmo valor em todo retry.
public sealed record NovaCobranca(Guid PagamentoId, decimal Valor, string Moeda, string EmailPagador, string Descricao,
    DateTimeOffset ExpiraEm, string ChaveIdempotencia);

// Resposta do provedor ou falha técnica (timeout, 5xx, 429, rede, resposta ilegível). Falha técnica nunca é recusa:
// o resultado fica desconhecido e segue para a reconciliação.
public sealed record ResultadoProvedor<T>
{
    public T? Valor { get; private init; }
    public string? Falha { get; private init; }
    public bool Ok => Falha is null;

    public static ResultadoProvedor<T> Sucesso(T? valor) => new() { Valor = valor };

    public static ResultadoProvedor<T> FalhaTecnica(string motivo) => new() { Falha = motivo };
}

public interface IProvedorPagamento
{
    Task<ResultadoProvedor<PagamentoNoProvedor>> CriarAsync(NovaCobranca cobranca, CancellationToken ct = default);

    // Valor nulo: o provedor não conhece o pagamento.
    Task<ResultadoProvedor<PagamentoNoProvedor>> ObterAsync(string referencia, CancellationToken ct = default);

    // Procura pelo external_reference antes de refazer uma criação sem resposta. Valor nulo: não existe.
    Task<ResultadoProvedor<PagamentoNoProvedor>> BuscarPorPagamentoAsync(Guid pagamentoId, CancellationToken ct = default);

    // Estorno total.
    Task<ResultadoProvedor<EstornoNoProvedor>> EstornarAsync(string referencia, decimal valor, string chaveIdempotencia, CancellationToken ct = default);
}
