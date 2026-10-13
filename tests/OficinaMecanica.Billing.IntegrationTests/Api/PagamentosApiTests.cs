using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Webhook;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;

namespace OficinaMecanica.Billing.IntegrationTests.Api;

// Testes de API do CARD-39b: webhook assinado, consulta do Pix e simulador, contra a API e o banco reais,
// com o provedor simulado (MercadoPago:Modo=Simulado).
[Collection(PostgresCollection.Nome)]
public class PagamentosApiTests : IAsyncLifetime
{
    private const string Webhook = "/billing/pagamentos/webhook/mercadopago";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly BillingApiFactory _factory;
    private readonly Guid _clienteId = Guid.NewGuid();

    public PagamentosApiTests(PostgresFixture fixture) => _factory = new BillingApiFactory(fixture.NovoBancoVazio());

    public Task InitializeAsync()
    {
        _factory.CreateClient().Dispose();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // Orçamento aprovado e cobrança criada pelo comando da Saga, como no fluxo real.
    private async Task<Pagamento> PagamentoPendenteAsync()
    {
        var agora = DateTimeOffset.UtcNow;
        var orcamento = Orcamento.Criar(Guid.NewGuid(), _clienteId, Guid.NewGuid(), Guid.NewGuid(), $"orcamento:{Guid.NewGuid():N}", "BRL",
            agora.AddMinutes(-10), [new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Servico, "Troca de óleo", 1, 125.90m)],
            agora, Montagem.Validade);
        orcamento.Decidir(1, ResultadoDecisao.Aprovado, _clienteId, null, agora);
        await using (var db = PostgresFixture.CriarContexto(_factory.ConnectionString))
        {
            db.Orcamentos.Add(orcamento);
            await db.SaveChangesAsync();
        }

        using (var escopo = _factory.Services.CreateScope())
        {
            var processar = escopo.ServiceProvider.GetRequiredService<ProcessarMensagemSagaUseCase>();
            var pedido = Montagem.PedidoCobranca(orcamento.OsId, orcamento.FilialId, orcamento.Id, 125.90m);
            Assert.Equal(StatusProcessamento.Processada, (await processar.ExecutarAsync(Montagem.CanalCobranca, Montagem.Bytes(pedido))).Status);
        }

        await using var leitura = PostgresFixture.CriarContexto(_factory.ConnectionString);
        return await leitura.Pagamentos.SingleAsync(p => p.OsId == orcamento.OsId);
    }

    private HttpRequestMessage Notificacao(string dataId, string requestId, string? assinatura, string tipo = "payment")
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Post, $"{Webhook}?data.id={dataId}&type={tipo}")
        {
            Content = new StringContent($$$"""{"action":"payment.updated","type":"{{{tipo}}}","data":{"id":"{{{dataId}}}"}}""", Encoding.UTF8, "application/json")
        };
        requisicao.Headers.Add("x-request-id", requestId);
        if (assinatura is not null)
            requisicao.Headers.Add("x-signature", assinatura);
        return requisicao;
    }

    private HttpRequestMessage NotificacaoAssinada(string dataId, string? requestId = null, DateTimeOffset? quando = null, string tipo = "payment")
    {
        var id = requestId ?? Guid.NewGuid().ToString();
        var assinatura = ValidadorAssinaturaMercadoPago.Assinar(BillingApiFactory.SegredoWebhook, dataId, id,
            (quando ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        return Notificacao(dataId, id, assinatura, tipo);
    }

    private async Task AprovarNoSimuladorAsync(string referencia)
    {
        var resposta = await _factory.Funcionario("Admin").PostAsJsonAsync($"/billing/simulador/pagamentos/{referencia}/status", new { status = "approved" });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    private async Task<int> ContarAsync(string messageType)
    {
        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        return await db.Outbox.CountAsync(m => m.MessageType == messageType);
    }

    // ── Webhook ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WebhookSemAssinatura_401SemGravarNada()
    {
        var pagamento = await PagamentoPendenteAsync();

        var resposta = await _factory.CreateClient().SendAsync(Notificacao(pagamento.ReferenciaProvedor!, "req-1", null));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal(0, await db.NotificacoesPagamento.CountAsync());
    }

    [Fact]
    public async Task WebhookAdulterado_401EPagamentoInalterado()
    {
        var pagamento = await PagamentoPendenteAsync();
        var assinaturaDeOutroRecurso = ValidadorAssinaturaMercadoPago.Assinar(BillingApiFactory.SegredoWebhook, "outro", "req-1",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var resposta = await _factory.CreateClient().SendAsync(Notificacao(pagamento.ReferenciaProvedor!, "req-1", assinaturaDeOutroRecurso));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal(StatusPagamento.Pendente, (await db.Pagamentos.SingleAsync()).Status);
    }

    [Fact]
    public async Task WebhookComAssinaturaAntiga_401()
    {
        var pagamento = await PagamentoPendenteAsync();

        var resposta = await _factory.CreateClient().SendAsync(NotificacaoAssinada(pagamento.ReferenciaProvedor!, quando: DateTimeOffset.UtcNow.AddHours(-2)));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task WebhookValido_ConsultaOProvedorEPublicaAprovacaoUmaVez()
    {
        var pagamento = await PagamentoPendenteAsync();
        var simulado = _factory.Services.GetRequiredService<ProvedorPagamentoSimulado>();
        simulado.DefinirStatus(pagamento.ReferenciaProvedor!, "approved");
        var http = _factory.CreateClient();

        var primeira = await http.SendAsync(NotificacaoAssinada(pagamento.ReferenciaProvedor!, "req-a"));
        var replay = await http.SendAsync(NotificacaoAssinada(pagamento.ReferenciaProvedor!, "req-a"));
        var outra = await http.SendAsync(NotificacaoAssinada(pagamento.ReferenciaProvedor!, "req-b"));

        Assert.All([primeira, replay, outra], r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await ContarAsync("PaymentApproved.v1"));
        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal(StatusPagamento.Aprovado, (await db.Pagamentos.SingleAsync()).Status);
        Assert.Equal(2, await db.NotificacoesPagamento.CountAsync());
    }

    [Fact]
    public async Task WebhookSemMudancaNoProvedor_NaoAprova()
    {
        // Notificação legítima, mas o provedor ainda diz pending: o corpo do webhook não muda estado.
        var pagamento = await PagamentoPendenteAsync();

        var resposta = await _factory.CreateClient().SendAsync(NotificacaoAssinada(pagamento.ReferenciaProvedor!));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(0, await ContarAsync("PaymentApproved.v1"));
    }

    [Fact]
    public async Task WebhookDeOutroTipo_Ignorado()
    {
        var pagamento = await PagamentoPendenteAsync();

        var resposta = await _factory.CreateClient().SendAsync(NotificacaoAssinada(pagamento.ReferenciaProvedor!, tipo: "merchant_order"));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal(0, await db.NotificacoesPagamento.CountAsync());
    }

    [Fact]
    public async Task WebhookSemRequestId_400()
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Post, $"{Webhook}?data.id=123&type=payment") { Content = new StringContent("{}") };

        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(requisicao)).StatusCode);
    }

    // ── Consulta ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClienteVeOCodigoPixDoProprioPagamento()
    {
        var pagamento = await PagamentoPendenteAsync();

        var meus = await _factory.Cliente(_clienteId).GetFromJsonAsync<List<PagamentoResponse>>("/billing/pagamentos/meus", _json);
        var deOutro = await _factory.Cliente(Guid.NewGuid()).GetFromJsonAsync<List<PagamentoResponse>>("/billing/pagamentos/meus", _json);

        var meu = Assert.Single(meus!);
        Assert.Equal(pagamento.Id, meu.PaymentId);
        Assert.Equal(StatusPagamento.Pendente, meu.Status);
        Assert.StartsWith("00020126", meu.CodigoPix);
        Assert.Empty(deOutro!);
    }

    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Atendente", HttpStatusCode.OK)]
    [InlineData("Mecanico", HttpStatusCode.Forbidden)]
    public async Task PagamentosDaOs_SoAtendimento(string perfil, HttpStatusCode esperado)
    {
        var pagamento = await PagamentoPendenteAsync();

        Assert.Equal(esperado, (await _factory.Funcionario(perfil).GetAsync($"/billing/pagamentos/os/{pagamento.OsId}")).StatusCode);
    }

    // ── Simulador ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Simulador_AprovaComoOClientePagando()
    {
        var pagamento = await PagamentoPendenteAsync();

        await AprovarNoSimuladorAsync(pagamento.ReferenciaProvedor!);

        var meus = await _factory.Cliente(_clienteId).GetFromJsonAsync<List<PagamentoResponse>>("/billing/pagamentos/meus", _json);
        Assert.Equal(StatusPagamento.Aprovado, Assert.Single(meus!).Status);
        Assert.Equal(1, await ContarAsync("PaymentApproved.v1"));
    }

    [Fact]
    public async Task Simulador_SoAdminEStatusConhecido()
    {
        var pagamento = await PagamentoPendenteAsync();
        var rota = $"/billing/simulador/pagamentos/{pagamento.ReferenciaProvedor}/status";

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.Cliente(_clienteId).PostAsJsonAsync(rota, new { status = "approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.Funcionario("Admin").PostAsJsonAsync(rota, new { status = "pago" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.Funcionario("Admin").PostAsJsonAsync("/billing/simulador/pagamentos/000/status", new { status = "approved" })).StatusCode);
    }
}
