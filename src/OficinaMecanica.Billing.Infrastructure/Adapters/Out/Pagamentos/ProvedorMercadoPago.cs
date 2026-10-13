using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

// Cliente da API de Pagamentos do Mercado Pago para Pix (ADR-019), escrito com HttpClient para controlar
// cabeçalhos, timeout e retry. Timeout, rede, 5xx e 429 são repetidos com espera exponencial; depois, e para
// qualquer outra resposta inesperada, o resultado é falha técnica. Nunca registra token, e-mail ou corpo da resposta.
public class ProvedorMercadoPago(HttpClient http, IOptions<MercadoPagoOptions> options, ILogger<ProvedorMercadoPago> logger) : IProvedorPagamento
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly MercadoPagoOptions _opcoes = options.Value;

    // Espera antes da tentativa n (1, 2...): base × 2^(n-1).
    public TimeSpan EsperaBase { get; init; } = TimeSpan.FromMilliseconds(200);

    public static void Configurar(HttpClient http, MercadoPagoOptions opcoes)
    {
        http.BaseAddress = new Uri(opcoes.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(opcoes.TimeoutSegundos);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.AccessToken);
    }

    public Task<ResultadoProvedor<PagamentoNoProvedor>> CriarAsync(NovaCobranca cobranca, CancellationToken ct = default)
        => EnviarAsync<PagamentoMp, PagamentoNoProvedor>("criar", () =>
        {
            var requisicao = new HttpRequestMessage(HttpMethod.Post, "v1/payments")
            {
                Content = JsonContent.Create(new CriacaoMp(
                    cobranca.Valor,
                    cobranca.Descricao,
                    "pix",
                    cobranca.PagamentoId.ToString(),
                    cobranca.ExpiraEm.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+00:00'", CultureInfo.InvariantCulture),
                    new PagadorMp(cobranca.EmailPagador),
                    string.IsNullOrWhiteSpace(_opcoes.UrlNotificacao) ? null : _opcoes.UrlNotificacao), options: _json)
            };
            requisicao.Headers.Add("X-Idempotency-Key", cobranca.ChaveIdempotencia);
            return requisicao;
        }, Mapear, ct);

    public Task<ResultadoProvedor<PagamentoNoProvedor>> ObterAsync(string referencia, CancellationToken ct = default)
        => EnviarAsync<PagamentoMp, PagamentoNoProvedor>("consultar",
            () => new HttpRequestMessage(HttpMethod.Get, $"v1/payments/{Uri.EscapeDataString(referencia)}"), Mapear, ct);

    public Task<ResultadoProvedor<PagamentoNoProvedor>> BuscarPorPagamentoAsync(Guid pagamentoId, CancellationToken ct = default)
        => EnviarAsync<BuscaMp, PagamentoNoProvedor>("buscar",
            () => new HttpRequestMessage(HttpMethod.Get, $"v1/payments/search?external_reference={pagamentoId}&sort=date_created&criteria=desc"),
            busca => busca.Results is { Count: > 0 } resultados ? Mapear(resultados[0]) : null, ct);

    public Task<ResultadoProvedor<EstornoNoProvedor>> EstornarAsync(string referencia, decimal valor, string chaveIdempotencia, CancellationToken ct = default)
        => EnviarAsync<EstornoMp, EstornoNoProvedor>("estornar", () =>
        {
            var requisicao = new HttpRequestMessage(HttpMethod.Post, $"v1/payments/{Uri.EscapeDataString(referencia)}/refunds")
            {
                Content = JsonContent.Create(new { amount = valor })
            };
            requisicao.Headers.Add("X-Idempotency-Key", chaveIdempotencia);
            // Sem este cabeçalho, estorno Pix em contingência pode não voltar como in_process (documentação de reembolso Pix).
            requisicao.Headers.Add("X-Render-In-Process-Refunds", "true");
            return requisicao;
        }, estorno => new EstornoNoProvedor(estorno.Id.ToString(CultureInfo.InvariantCulture), MapearEstorno(estorno.Status), estorno.Status ?? "desconhecido"), ct);

    // Status do pagamento no Mercado Pago → status que Billing entende.
    public static StatusNoProvedor MapearStatus(string? status) => status switch
    {
        "pending" or "in_process" or "authorized" => StatusNoProvedor.Pendente,
        "approved" => StatusNoProvedor.Aprovado,
        "rejected" or "cancelled" => StatusNoProvedor.Recusado,
        "refunded" => StatusNoProvedor.Estornado,
        _ => StatusNoProvedor.ForaDoFluxo
    };

    public static StatusEstornoNoProvedor MapearEstorno(string? status) => status switch
    {
        "approved" => StatusEstornoNoProvedor.Aprovado,
        "rejected" or "cancelled" => StatusEstornoNoProvedor.Recusado,
        _ => StatusEstornoNoProvedor.Pendente
    };

    private static PagamentoNoProvedor Mapear(PagamentoMp p) => new(
        p.Id.ToString(CultureInfo.InvariantCulture),
        p.ExternalReference,
        p.TransactionAmount,
        p.CurrencyId ?? string.Empty,
        MapearStatus(p.Status),
        p.Status ?? "desconhecido",
        p.DateApproved,
        p.PointOfInteraction?.TransactionData?.QrCode,
        p.PointOfInteraction?.TransactionData?.TicketUrl);

    private async Task<ResultadoProvedor<TResultado>> EnviarAsync<TResposta, TResultado>(string operacao, Func<HttpRequestMessage> criarRequisicao,
        Func<TResposta, TResultado?> mapear, CancellationToken ct)
    {
        string falha = "Sem tentativa.";
        for (var tentativa = 1; tentativa <= _opcoes.TentativasPorChamada; tentativa++)
        {
            if (tentativa > 1)
                await Task.Delay(EsperaBase * Math.Pow(2, tentativa - 2), ct);

            HttpResponseMessage resposta;
            try
            {
                using var requisicao = criarRequisicao();
                resposta = await http.SendAsync(requisicao, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                falha = ex is TaskCanceledException ? "Timeout." : $"Erro de rede: {ex.GetType().Name}.";
                logger.LogWarning("Mercado Pago {Operacao}: {Falha} Tentativa {Tentativa}.", operacao, falha, tentativa);
                continue;
            }

            using (resposta)
            {
                var codigo = (int)resposta.StatusCode;
                if (resposta.IsSuccessStatusCode)
                {
                    try
                    {
                        var corpo = await resposta.Content.ReadFromJsonAsync<TResposta>(_json, ct);
                        MetricasPagamento.Chamada(operacao, "sucesso");
                        return ResultadoProvedor<TResultado>.Sucesso(corpo is null ? default : mapear(corpo));
                    }
                    catch (JsonException)
                    {
                        // Corpo ilegível depois de um 2xx: a operação pode ter acontecido. Não repete; a reconciliação consulta.
                        MetricasPagamento.Chamada(operacao, "falha");
                        return ResultadoProvedor<TResultado>.FalhaTecnica($"Resposta {codigo} ilegível.");
                    }
                }

                if (resposta.StatusCode == HttpStatusCode.NotFound && operacao is "consultar")
                {
                    MetricasPagamento.Chamada(operacao, "nao_encontrado");
                    return ResultadoProvedor<TResultado>.Sucesso(default);
                }

                falha = $"HTTP {codigo}.";
                logger.LogWarning("Mercado Pago {Operacao}: {Falha} Tentativa {Tentativa}.", operacao, falha, tentativa);
                if (codigo != 429 && codigo < 500)
                    break;
            }
        }

        MetricasPagamento.Chamada(operacao, "falha");
        return ResultadoProvedor<TResultado>.FalhaTecnica(falha);
    }

    // Formato da API de Pagamentos (snake_case). Só os campos que Billing usa.
    private sealed record CriacaoMp(decimal TransactionAmount, string Description, string PaymentMethodId, string ExternalReference,
        string DateOfExpiration, PagadorMp Payer, string? NotificationUrl);

    private sealed record PagadorMp(string Email);

    private sealed record PagamentoMp(long Id, string? Status, string? ExternalReference, decimal TransactionAmount, string? CurrencyId,
        DateTimeOffset? DateApproved, PontoDeInteracaoMp? PointOfInteraction);

    private sealed record PontoDeInteracaoMp(DadosTransacaoMp? TransactionData);

    private sealed record DadosTransacaoMp(string? QrCode, string? TicketUrl);

    private sealed record BuscaMp(List<PagamentoMp>? Results);

    private sealed record EstornoMp(long Id, string? Status);
}
