using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace OficinaMecanica.Billing.IntegrationTests.Api;

// Sobe a API real (Program.cs e migrations) contra um banco PostgreSQL vazio do Testcontainers.
public class BillingApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    private const string JwtKey = "chave-jwt-de-teste-com-mais-de-32-caracteres";
    private const string JwtIssuer = "oficina-atendimento";
    private const string JwtAudience = "oficina-atendimento-api";
    public const string SegredoWebhook = "segredo-do-webhook-de-teste";

    public string ConnectionString => connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("RabbitMq:Enabled", "false");
        // Os testes de API controlam a expiração pela data de criação; o serviço em segundo plano fica desligado.
        builder.UseSetting("Orcamento:ExpiracaoHabilitada", "false");
        // Provedor simulado (padrão) e reconciliação desligada: os testes disparam webhook e simulador explicitamente.
        builder.UseSetting("MercadoPago:WebhookSecret", SegredoWebhook);
        builder.UseSetting("MercadoPago:ReconciliacaoHabilitada", "false");
        builder.UseSetting("Jaeger:Endpoint", "http://localhost:4318");
    }

    // Token no formato emitido pelo OS para funcionários (sub, email, role), mesma chave e issuer (ADR-015).
    public HttpClient Funcionario(string perfil) => ComToken(
    [
        new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
        new Claim(JwtRegisteredClaimNames.Email, $"{perfil.ToLowerInvariant()}@oficina.example"),
        new Claim("role", perfil)
    ]);

    // Token no formato da Lambda de CPF: role Cliente e cliente_id.
    public HttpClient Cliente(Guid clienteId) => ComToken([new Claim("cliente_id", clienteId.ToString()), new Claim("role", "Cliente")]);

    // Token de cliente sem cliente_id: não identifica o dono.
    public HttpClient ClienteSemId() => ComToken([new Claim("role", "Cliente")]);

    private HttpClient ComToken(Claim[] claims)
    {
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));

        var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return http;
    }
}
