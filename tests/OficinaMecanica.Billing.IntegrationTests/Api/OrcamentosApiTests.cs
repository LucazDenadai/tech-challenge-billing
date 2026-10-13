using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;

namespace OficinaMecanica.Billing.IntegrationTests.Api;

// Testes de API do CARD-39a: decisão só do cliente dono, idempotência, 409/410 e consultas, contra a API e o banco reais.
[Collection(PostgresCollection.Nome)]
public class OrcamentosApiTests : IAsyncLifetime
{
    private readonly BillingApiFactory _factory;

    public OrcamentosApiTests(PostgresFixture fixture) => _factory = new BillingApiFactory(fixture.NovoBancoVazio());

    // Sobe a API antes de semear: o Program.cs aplica as migrations.
    public Task InitializeAsync()
    {
        _factory.CreateClient().Dispose();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Orcamento> SemearAsync(Guid clienteId, DateTimeOffset? criadoEm = null)
    {
        var orcamento = Orcamento.Criar(Guid.NewGuid(), clienteId, Guid.NewGuid(), Guid.NewGuid(), $"orcamento:{Guid.NewGuid():N}", "BRL",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            [new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Peca, "Filtro de óleo", 1, 45.90m),
             new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Servico, "Troca de óleo", 1, 80m)],
            criadoEm ?? DateTimeOffset.UtcNow, Montagem.Validade);

        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        db.Orcamentos.Add(orcamento);
        await db.SaveChangesAsync();
        return orcamento;
    }

    private static string Aprovar(Guid quoteId) => $"/billing/orcamentos/{quoteId}/aprovar";

    private static string Recusar(Guid quoteId) => $"/billing/orcamentos/{quoteId}/recusar";

    // ── Saúde ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthEReady_Respondem200()
    {
        var http = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/billing/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/billing/ready")).StatusCode);
    }

    // ── Quem decide ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SemToken_Retorna401()
    {
        var orcamento = await SemearAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 })).StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Atendente")]
    [InlineData("Mecanico")]
    public async Task FuncionarioNaoDecide_Retorna403SemMudarNada(string perfil)
    {
        var orcamento = await SemearAsync(Guid.NewGuid());
        var http = _factory.Funcionario(perfil);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(Recusar(orcamento.Id), new { versao = 1 })).StatusCode);

        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal(StatusVersaoOrcamento.AguardandoDecisao, (await db.Set<VersaoOrcamento>().SingleAsync()).Status);
        Assert.Equal(0, await db.Outbox.CountAsync());
    }

    [Fact]
    public async Task ClienteSemClienteIdNoToken_Retorna403()
    {
        var orcamento = await SemearAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClienteSemId().PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 })).StatusCode);
    }

    [Fact]
    public async Task OutroCliente_Retorna404SemMudarNada()
    {
        var orcamento = await SemearAsync(Guid.NewGuid());
        var outro = _factory.Cliente(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await outro.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.GetAsync($"/billing/orcamentos/{orcamento.Id}")).StatusCode);

        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal(0, await db.Set<DecisaoOrcamento>().CountAsync());
    }

    // ── Decisão ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClienteDonoAprova_200IdempotenteEEventoUmaVez()
    {
        var clienteId = Guid.NewGuid();
        var orcamento = await SemearAsync(clienteId);
        var http = _factory.Cliente(clienteId);

        var primeira = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 });
        var segunda = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 });

        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        var corpo = await primeira.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Aprovada", corpo.GetProperty("versaoVigente").GetProperty("status").GetString());
        Assert.Equal(125.90m, corpo.GetProperty("versaoVigente").GetProperty("valorTotal").GetDecimal());

        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal("QuoteApproved.v1", (await db.Outbox.SingleAsync()).MessageType);
        Assert.Equal(1, await db.Set<DecisaoOrcamento>().CountAsync());
    }

    [Fact]
    public async Task ClienteDonoRecusa_GuardaMotivoEDecisaoOpostaDepoisE409()
    {
        var clienteId = Guid.NewGuid();
        var orcamento = await SemearAsync(clienteId);
        var http = _factory.Cliente(clienteId);

        var recusa = await http.PostAsJsonAsync(Recusar(orcamento.Id), new { versao = 1, motivo = "Vou pensar" });
        var aprovacao = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 });

        Assert.Equal(HttpStatusCode.OK, recusa.StatusCode);
        var decisao = (await recusa.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decisoes")[0];
        Assert.Equal("Vou pensar", decisao.GetProperty("motivo").GetString());
        Assert.Equal(HttpStatusCode.Conflict, aprovacao.StatusCode);

        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal("QuoteDeclined.v1", (await db.Outbox.SingleAsync()).MessageType);
    }

    [Fact]
    public async Task OrcamentoExpirado_410EQuoteExpiredUmaVez()
    {
        var clienteId = Guid.NewGuid();
        var orcamento = await SemearAsync(clienteId, DateTimeOffset.UtcNow - Montagem.Validade - TimeSpan.FromMinutes(1));
        var http = _factory.Cliente(clienteId);

        var primeira = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 });
        var segunda = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 });

        Assert.Equal(HttpStatusCode.Gone, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, segunda.StatusCode);
        await using var db = PostgresFixture.CriarContexto(_factory.ConnectionString);
        Assert.Equal("QuoteExpired.v1", (await db.Outbox.SingleAsync()).MessageType);
        Assert.Equal(0, await db.Set<DecisaoOrcamento>().CountAsync());
    }

    [Fact]
    public async Task VersaoAntiga_409ComVersaoVigente_EVersaoInexistente404()
    {
        var clienteId = Guid.NewGuid();
        var orcamento = await SemearAsync(clienteId);
        await using (var db = PostgresFixture.CriarContexto(_factory.ConnectionString))
        {
            var carregado = await db.Orcamentos.Include(o => o.Versoes).ThenInclude(v => v.Itens).SingleAsync(o => o.Id == orcamento.Id);
            carregado.NovaVersao("orcamento:v2", "BRL", DateTimeOffset.UtcNow,
                [new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Peca, "Filtro de óleo", 2, 45.90m)], DateTimeOffset.UtcNow, Montagem.Validade);
            await db.SaveChangesAsync();
        }
        var http = _factory.Cliente(clienteId);

        var antiga = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 1 });
        var inexistente = await http.PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 9 });

        Assert.Equal(HttpStatusCode.Conflict, antiga.StatusCode);
        Assert.Equal(2, (await antiga.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versaoVigente").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task VersaoInvalidaNoCorpo_Retorna400()
    {
        var clienteId = Guid.NewGuid();
        var orcamento = await SemearAsync(clienteId);

        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.Cliente(clienteId).PostAsJsonAsync(Aprovar(orcamento.Id), new { versao = 0 })).StatusCode);
    }

    // ── Consultas ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Meus_ListaSoOsDoClienteDoToken()
    {
        var clienteId = Guid.NewGuid();
        var meu = await SemearAsync(clienteId);
        await SemearAsync(Guid.NewGuid());

        var lista = await _factory.Cliente(clienteId).GetFromJsonAsync<JsonElement>("/billing/orcamentos/meus");

        Assert.Equal(meu.Id, Assert.Single(lista.EnumerateArray()).GetProperty("quoteId").GetGuid());
    }

    [Fact]
    public async Task Atendimento_ConsultaPorOsEPorId_ClienteNaoConsultaPorOs()
    {
        var orcamento = await SemearAsync(Guid.NewGuid());

        var atendente = _factory.Funcionario("Atendente");
        Assert.Equal(orcamento.Id, (await atendente.GetFromJsonAsync<JsonElement>($"/billing/orcamentos/os/{orcamento.OsId}")).GetProperty("quoteId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await atendente.GetAsync($"/billing/orcamentos/{orcamento.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await atendente.GetAsync($"/billing/orcamentos/os/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.Cliente(orcamento.ClienteId).GetAsync($"/billing/orcamentos/os/{orcamento.OsId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.Funcionario("Mecanico").GetAsync($"/billing/orcamentos/{orcamento.Id}")).StatusCode);
    }

    [Fact]
    public async Task Swagger_DescreveAsRotasDoOrcamentoEDoPagamento()
    {
        var swagger = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/billing/swagger/v1/swagger.json");

        var rotas = swagger.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/billing/orcamentos/meus", rotas);
        Assert.Contains("/billing/orcamentos/{quoteId}", rotas);
        Assert.Contains("/billing/orcamentos/os/{osId}", rotas);
        Assert.Contains("/billing/orcamentos/{quoteId}/aprovar", rotas);
        Assert.Contains("/billing/orcamentos/{quoteId}/recusar", rotas);
        // Criação só pela Saga (QuoteRequested): não há POST em /billing/orcamentos.
        Assert.False(swagger.GetProperty("paths").TryGetProperty("/billing/orcamentos", out _));
        // Pagamento (CARD-39b): consulta, webhook e simulador; a cobrança também só nasce pela Saga.
        Assert.Contains("/billing/pagamentos/meus", rotas);
        Assert.Contains("/billing/pagamentos/os/{osId}", rotas);
        Assert.Contains("/billing/pagamentos/webhook/mercadopago", rotas);
        Assert.Contains("/billing/simulador/pagamentos/{referencia}/status", rotas);
        Assert.False(swagger.GetProperty("paths").TryGetProperty("/billing/pagamentos", out _));

        // Evidência dos CARD-39a e 39b: com EXPORTAR_OPENAPI definido, grava o OpenAPI gerado nesse caminho (docs/openapi/billing-v1.json).
        if (Environment.GetEnvironmentVariable("EXPORTAR_OPENAPI") is { Length: > 0 } caminho)
            await File.WriteAllTextAsync(caminho, JsonSerializer.Serialize(swagger, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
