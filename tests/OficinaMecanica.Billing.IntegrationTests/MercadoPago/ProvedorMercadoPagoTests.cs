using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

namespace OficinaMecanica.Billing.IntegrationTests.MercadoPago;

// Cliente HTTP do Mercado Pago contra respostas gravadas no formato da API de Pagamentos (sem rede, sem credenciais).
public class ProvedorMercadoPagoTests
{
    private const string Token = "TEST-token-de-teste";

    private readonly RespostasGravadas _respostas = new();

    private ProvedorMercadoPago Provedor(int tentativas = 3, int timeoutSegundos = 10)
    {
        var opcoes = new MercadoPagoOptions
        {
            Modo = ModoProvedor.MercadoPago, AccessToken = Token, WebhookSecret = "s",
            BaseUrl = "https://api.mercadopago.test", TentativasPorChamada = tentativas, TimeoutSegundos = timeoutSegundos
        };
        var http = new HttpClient(_respostas);
        ProvedorMercadoPago.Configurar(http, opcoes);
        return new ProvedorMercadoPago(http, Options.Create(opcoes), NullLogger<ProvedorMercadoPago>.Instance) { EsperaBase = TimeSpan.FromMilliseconds(1) };
    }

    private static NovaCobranca Cobranca(Guid id) => new(id, 125.90m, "BRL", "cliente@exemplo.com", "Ordem de serviço X",
        new DateTimeOffset(2026, 10, 13, 12, 30, 0, TimeSpan.Zero), id.ToString());

    // Resposta de POST/GET /v1/payments reduzida aos campos usados, como na documentação do Pix.
    private static string PagamentoJson(Guid id, string status = "pending", decimal valor = 125.90m) => $$"""
        {
          "id": 1325734520,
          "status": "{{status}}",
          "status_detail": "pending_waiting_transfer",
          "external_reference": "{{id}}",
          "transaction_amount": {{valor.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "currency_id": "BRL",
          "date_approved": {{(status == "approved" ? "\"2026-10-13T09:05:00.000-03:00\"" : "null")}},
          "point_of_interaction": { "transaction_data": { "qr_code": "00020126580014br.gov.bcb.pix", "ticket_url": "https://www.mercadopago.com.br/payments/1325734520/ticket" } }
        }
        """;

    [Fact]
    public async Task Criar_EnviaPixComChaveIdempotenteETokenEMapeiaAResposta()
    {
        var id = Guid.NewGuid();
        _respostas.Enfileirar(HttpStatusCode.Created, PagamentoJson(id));

        var resultado = await Provedor().CriarAsync(Cobranca(id));

        Assert.True(resultado.Ok);
        Assert.Equal(new PagamentoNoProvedor("1325734520", id.ToString(), 125.90m, "BRL", StatusNoProvedor.Pendente, "pending", null,
            "00020126580014br.gov.bcb.pix", "https://www.mercadopago.com.br/payments/1325734520/ticket"), resultado.Valor);

        var requisicao = Assert.Single(_respostas.Recebidas);
        Assert.Equal(HttpMethod.Post, requisicao.Metodo);
        Assert.Equal("https://api.mercadopago.test/v1/payments", requisicao.Url);
        Assert.Equal($"Bearer {Token}", requisicao.Cabecalhos["Authorization"]);
        Assert.Equal(id.ToString(), requisicao.Cabecalhos["X-Idempotency-Key"]);
        var corpo = JsonNode.Parse(requisicao.Corpo!)!;
        Assert.Equal(125.90m, corpo["transaction_amount"]!.GetValue<decimal>());
        Assert.Equal("pix", corpo["payment_method_id"]!.GetValue<string>());
        Assert.Equal(id.ToString(), corpo["external_reference"]!.GetValue<string>());
        Assert.Equal("cliente@exemplo.com", corpo["payer"]!["email"]!.GetValue<string>());
        Assert.Equal("2026-10-13T12:30:00.000+00:00", corpo["date_of_expiration"]!.GetValue<string>());
        Assert.Null(corpo["notification_url"]);
    }

    [Fact]
    public async Task ErroTemporario_RepeteComAMesmaChave()
    {
        var id = Guid.NewGuid();
        _respostas.Enfileirar(HttpStatusCode.ServiceUnavailable, "{}");
        _respostas.Enfileirar((HttpStatusCode)429, "{}");
        _respostas.Enfileirar(HttpStatusCode.Created, PagamentoJson(id));

        var resultado = await Provedor().CriarAsync(Cobranca(id));

        Assert.True(resultado.Ok);
        Assert.Equal(3, _respostas.Recebidas.Count);
        Assert.Single(_respostas.Recebidas.Select(r => r.Cabecalhos["X-Idempotency-Key"]).Distinct());
    }

    [Fact]
    public async Task ErroTemporarioPersistente_FalhaTecnicaNuncaRecusa()
    {
        for (var i = 0; i < 3; i++)
            _respostas.Enfileirar(HttpStatusCode.InternalServerError, "{}");

        var resultado = await Provedor().CriarAsync(Cobranca(Guid.NewGuid()));

        Assert.False(resultado.Ok);
        Assert.Equal("HTTP 500.", resultado.Falha);
        Assert.Equal(3, _respostas.Recebidas.Count);
    }

    [Fact]
    public async Task ErroDoCliente_FalhaTecnicaSemRepetir()
    {
        _respostas.Enfileirar(HttpStatusCode.BadRequest, """{"message":"invalid payer email"}""");

        var resultado = await Provedor().CriarAsync(Cobranca(Guid.NewGuid()));

        Assert.False(resultado.Ok);
        Assert.Single(_respostas.Recebidas);
    }

    [Fact]
    public async Task Timeout_FalhaTecnica()
    {
        _respostas.Atraso = TimeSpan.FromSeconds(5);
        _respostas.Enfileirar(HttpStatusCode.Created, "{}");

        var resultado = await Provedor(tentativas: 1, timeoutSegundos: 1).CriarAsync(Cobranca(Guid.NewGuid()));

        Assert.False(resultado.Ok);
        Assert.Equal("Timeout.", resultado.Falha);
    }

    [Fact]
    public async Task CorpoIlegivelDepoisDe2xx_FalhaTecnicaSemRepetir()
    {
        _respostas.Enfileirar(HttpStatusCode.Created, "<html>gateway</html>");

        var resultado = await Provedor().CriarAsync(Cobranca(Guid.NewGuid()));

        Assert.False(resultado.Ok);
        Assert.Single(_respostas.Recebidas);
    }

    [Fact]
    public async Task ConsultarInexistente_SucessoSemPagamento()
    {
        _respostas.Enfileirar(HttpStatusCode.NotFound, """{"message":"Payment not found"}""");

        var resultado = await Provedor().ObterAsync("999");

        Assert.True(resultado.Ok);
        Assert.Null(resultado.Valor);
        Assert.Equal("https://api.mercadopago.test/v1/payments/999", _respostas.Recebidas.Single().Url);
    }

    [Fact]
    public async Task ConsultarAprovado_TrazDataDeAprovacao()
    {
        var id = Guid.NewGuid();
        _respostas.Enfileirar(HttpStatusCode.OK, PagamentoJson(id, "approved"));

        var resultado = await Provedor().ObterAsync("1325734520");

        Assert.Equal(StatusNoProvedor.Aprovado, resultado.Valor!.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 5, 0, TimeSpan.Zero), resultado.Valor.AprovadoEm);
    }

    [Fact]
    public async Task BuscarPorPagamento_UsaExternalReference()
    {
        var id = Guid.NewGuid();
        _respostas.Enfileirar(HttpStatusCode.OK, $$"""{ "results": [ {{PagamentoJson(id)}} ], "paging": { "total": 1 } }""");
        _respostas.Enfileirar(HttpStatusCode.OK, """{ "results": [], "paging": { "total": 0 } }""");

        var encontrado = await Provedor().BuscarPorPagamentoAsync(id);
        var vazio = await Provedor().BuscarPorPagamentoAsync(Guid.NewGuid());

        Assert.Equal("1325734520", encontrado.Valor!.Referencia);
        Assert.Contains($"external_reference={id}", _respostas.Recebidas[0].Url);
        Assert.True(vazio.Ok);
        Assert.Null(vazio.Valor);
    }

    [Fact]
    public async Task Estornar_EnviaChaveECabecalhoDeContingenciaEMapeiaEmProcessamento()
    {
        _respostas.Enfileirar(HttpStatusCode.Created, """{ "id": 4567, "payment_id": 1325734520, "amount": 125.90, "status": "in_process" }""");

        var resultado = await Provedor().EstornarAsync("1325734520", 125.90m, "estorno-x");

        Assert.Equal(new EstornoNoProvedor("4567", StatusEstornoNoProvedor.Pendente, "in_process"), resultado.Valor);
        var requisicao = _respostas.Recebidas.Single();
        Assert.Equal("https://api.mercadopago.test/v1/payments/1325734520/refunds", requisicao.Url);
        Assert.Equal("estorno-x", requisicao.Cabecalhos["X-Idempotency-Key"]);
        Assert.Equal("true", requisicao.Cabecalhos["X-Render-In-Process-Refunds"]);
        Assert.Equal(125.90m, JsonNode.Parse(requisicao.Corpo!)!["amount"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("pending", StatusNoProvedor.Pendente)]
    [InlineData("in_process", StatusNoProvedor.Pendente)]
    [InlineData("authorized", StatusNoProvedor.Pendente)]
    [InlineData("approved", StatusNoProvedor.Aprovado)]
    [InlineData("rejected", StatusNoProvedor.Recusado)]
    [InlineData("cancelled", StatusNoProvedor.Recusado)]
    [InlineData("refunded", StatusNoProvedor.Estornado)]
    [InlineData("charged_back", StatusNoProvedor.ForaDoFluxo)]
    [InlineData("in_mediation", StatusNoProvedor.ForaDoFluxo)]
    [InlineData("status_novo", StatusNoProvedor.ForaDoFluxo)]
    public void MapeamentoDeStatus(string status, StatusNoProvedor esperado) => Assert.Equal(esperado, ProvedorMercadoPago.MapearStatus(status));

    [Fact]
    public async Task Simulado_CriacaoEEstornoIdempotentes()
    {
        var simulado = new ProvedorPagamentoSimulado();
        var id = Guid.NewGuid();

        var primeira = await simulado.CriarAsync(Cobranca(id));
        var segunda = await simulado.CriarAsync(Cobranca(id));
        Assert.Equal(primeira.Valor!.Referencia, segunda.Valor!.Referencia);

        var recusado = await simulado.EstornarAsync(primeira.Valor.Referencia, 125.90m, "e");
        Assert.Equal(StatusEstornoNoProvedor.Recusado, recusado.Valor!.Status);

        Assert.True(simulado.DefinirStatus(primeira.Valor.Referencia, "approved"));
        var estorno = await simulado.EstornarAsync(primeira.Valor.Referencia, 125.90m, "e2");
        var repetido = await simulado.EstornarAsync(primeira.Valor.Referencia, 125.90m, "e2");
        Assert.Equal(StatusEstornoNoProvedor.Aprovado, estorno.Valor!.Status);
        Assert.Equal(estorno.Valor, repetido.Valor);
        Assert.Equal(StatusNoProvedor.Estornado, (await simulado.ObterAsync(primeira.Valor.Referencia)).Valor!.Status);
    }

    private sealed record Requisicao(HttpMethod Metodo, string Url, Dictionary<string, string> Cabecalhos, string? Corpo);

    // HttpMessageHandler que devolve respostas enfileiradas e guarda o que recebeu.
    private sealed class RespostasGravadas : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _fila = new();

        public List<Requisicao> Recebidas { get; } = [];
        public TimeSpan Atraso { get; set; }

        public void Enfileirar(HttpStatusCode status, string corpo) => _fila.Enqueue((status, corpo));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cabecalhos = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var corpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Recebidas.Add(new Requisicao(request.Method, request.RequestUri!.ToString(), cabecalhos, corpo));

            if (Atraso > TimeSpan.Zero)
                await Task.Delay(Atraso, cancellationToken);

            var (status, conteudo) = _fila.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(conteudo, Encoding.UTF8, "application/json") };
        }
    }
}
