using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Agendamento;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

namespace OficinaMecanica.Billing.IntegrationTests.Infraestrutura;

// Validação de configuração na subida: valores inválidos derrubam o serviço em vez de rodar com padrão silencioso.
public class ConfiguracaoEBordasTests
{
    private const string Conexao = "Host=localhost;Database=x";

    private static IConfiguration Config(params (string Chave, string Valor)[] valores)
        => new ConfigurationBuilder().AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Chave, v.Valor))).Build();

    [Fact]
    public void Infraestrutura_SemConnectionString_NaoSobe()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(Config()));

        Assert.Contains("DefaultConnection", ex.Message);
    }

    [Fact]
    public void Infraestrutura_ValidadeDoOrcamentoNaoPositiva_NaoSobe()
    {
        var config = Config(("ConnectionStrings:DefaultConnection", Conexao), ("Orcamento:ValidadeHoras", "0"));

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(config));

        Assert.Contains("ValidadeHoras", ex.Message);
    }

    [Fact]
    public void Infraestrutura_PadroesSemSecoes_SimuladoComExpiracaoEReconciliacao()
    {
        var servicos = new ServiceCollection().AddInfrastructure(Config(("ConnectionStrings:DefaultConnection", Conexao)));

        Assert.Contains(servicos, s => s.ImplementationType == typeof(ExpiracaoOrcamentosHostedService));
        Assert.Contains(servicos, s => s.ImplementationType == typeof(ReconciliacaoPagamentosHostedService));
        Assert.Contains(servicos, s => s.ServiceType == typeof(ProvedorPagamentoSimulado));
    }

    [Fact]
    public void Infraestrutura_MercadoPagoReal_SemSimuladorNemAgendamentosDesligados()
    {
        var config = Config(("ConnectionStrings:DefaultConnection", Conexao), ("Orcamento:ExpiracaoHabilitada", "false"),
            ("MercadoPago:Modo", "MercadoPago"), ("MercadoPago:AccessToken", "TEST-token"), ("MercadoPago:WebhookSecret", "segredo"),
            ("MercadoPago:ReconciliacaoHabilitada", "false"));

        var servicos = new ServiceCollection().AddInfrastructure(config);

        Assert.DoesNotContain(servicos, s => s.ImplementationType == typeof(ExpiracaoOrcamentosHostedService));
        Assert.DoesNotContain(servicos, s => s.ImplementationType == typeof(ReconciliacaoPagamentosHostedService));
        Assert.DoesNotContain(servicos, s => s.ServiceType == typeof(ProvedorPagamentoSimulado));
        Assert.Contains(servicos, s => s.ServiceType == typeof(IProvedorPagamento));
    }

    public static TheoryData<Action<MercadoPagoOptions>, string> OpcoesInvalidas() => new()
    {
        { o => o.ExpiracaoMinutos = 29, "ExpiracaoMinutos" },
        { o => { o.Modo = ModoProvedor.MercadoPago; o.WebhookSecret = "s"; }, "AccessToken" },
        { o => { o.Modo = ModoProvedor.MercadoPago; o.AccessToken = "t"; }, "WebhookSecret" },
        { o => o.TimeoutSegundos = 0, "maiores que zero" },
        { o => o.TentativasPorChamada = 0, "maiores que zero" },
        { o => o.MaxTentativasReconciliacao = 0, "maiores que zero" },
        { o => o.IntervaloReconciliacaoSegundos = 0, "maiores que zero" },
        { o => o.IntervaloVerificacaoMinutos = 0, "maiores que zero" }
    };

    [Theory]
    [MemberData(nameof(OpcoesInvalidas))]
    public void OpcoesDoMercadoPagoInvalidas_NaoSobem(Action<MercadoPagoOptions> ajustar, string trecho)
    {
        var opcoes = new MercadoPagoOptions();
        ajustar(opcoes);

        Assert.Contains(trecho, Assert.Throws<InvalidOperationException>(opcoes.Validar).Message);
    }

    [Fact]
    public void Mensageria_SemSecao_NaoRegistraConsumidor()
    {
        var servicos = new ServiceCollection().AddMensageria(Config());

        Assert.DoesNotContain(servicos, s => s.ImplementationType == typeof(ConsumidorSagaHostedService));
    }

    [Theory]
    [InlineData("", "senha")]
    [InlineData("billing", "")]
    public void Mensageria_HabilitadaSemCredencial_NaoSobe(string usuario, string senha)
    {
        var config = Config(("RabbitMq:Enabled", "true"), ("RabbitMq:Username", usuario), ("RabbitMq:Password", senha));

        Assert.Contains("RabbitMq:Username", Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMensageria(config)).Message);
    }

    [Fact]
    public async Task HealthCheckDoConsumidor_SegueOEstado()
    {
        var estado = new EstadoConsumidorSaga();
        var check = new ConsumidorSagaHealthCheck(estado);

        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        estado.Marcar(true);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
