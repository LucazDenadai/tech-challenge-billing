using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Application.Ports.In;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Agendamento;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;

namespace OficinaMecanica.Billing.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Instância PostgreSQL dedicada a Billing (ADR-016); sem fallback para outro banco.
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não configurada.");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.AddSingleton(TimeProvider.System);
        var validadeHoras = configuration.GetValue("Orcamento:ValidadeHoras", 24);
        if (validadeHoras <= 0)
            throw new InvalidOperationException("Orcamento:ValidadeHoras deve ser maior que zero.");
        services.AddSingleton(new ConfiguracaoOrcamento(TimeSpan.FromHours(validadeHoras)));

        services.AddScoped<IUnidadeDeTrabalho, UnidadeDeTrabalho>();
        services.AddScoped<IOrcamentoRepository, OrcamentoRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IOutbox, OutboxRepository>();
        services.AddScoped<ITransacao, Transacao>();

        services.AddScoped<SolicitarOrcamentoUseCase>();
        services.AddScoped<DecidirOrcamentoUseCase>();
        services.AddScoped<ExpirarOrcamentosUseCase>();
        services.AddScoped<ConsultarOrcamentosUseCase>();
        services.AddScoped<IOrcamentoParaPagamento>(sp => sp.GetRequiredService<ConsultarOrcamentosUseCase>());
        services.AddScoped<ProcessarMensagemSagaUseCase>();

        AddPagamentos(services, configuration);

        var secaoExpiracao = configuration.GetSection(ExpiracaoOptions.Secao);
        services.Configure<ExpiracaoOptions>(secaoExpiracao);
        if ((secaoExpiracao.Get<ExpiracaoOptions>() ?? new ExpiracaoOptions()).ExpiracaoHabilitada)
            services.AddHostedService<ExpiracaoOrcamentosHostedService>();

        return services;
    }

    // Pagamento Pix pelo Mercado Pago ou pelo provedor simulado (ADR-019).
    private static void AddPagamentos(IServiceCollection services, IConfiguration configuration)
    {
        var secao = configuration.GetSection(MercadoPagoOptions.Secao);
        var opcoes = secao.Get<MercadoPagoOptions>() ?? new MercadoPagoOptions();
        opcoes.Validar();
        services.Configure<MercadoPagoOptions>(secao);

        services.AddSingleton(new ConfiguracaoPagamento(
            TimeSpan.FromMinutes(opcoes.ExpiracaoMinutos),
            TimeSpan.FromMinutes(opcoes.IntervaloVerificacaoMinutos),
            opcoes.MaxTentativasReconciliacao));

        services.AddScoped<IPagamentoRepository, PagamentoRepository>();
        services.AddScoped<INotificacaoPagamentoRepository, NotificacaoPagamentoRepository>();
        services.AddScoped<SolicitarPagamentoUseCase>();
        services.AddScoped<SolicitarEstornoUseCase>();
        services.AddScoped<CobrarPagamentoUseCase>();
        services.AddScoped<EstornarPagamentoUseCase>();
        services.AddScoped<ReconciliarPagamentoUseCase>();
        services.AddScoped<VerificarPagamentosUseCase>();
        services.AddScoped<ConsultarPagamentosUseCase>();

        if (opcoes.Modo == ModoProvedor.Simulado)
        {
            // Singleton: o "provedor" guarda os pagamentos em memória enquanto o serviço roda.
            services.AddSingleton<ProvedorPagamentoSimulado>();
            services.AddSingleton<IProvedorPagamento>(sp => sp.GetRequiredService<ProvedorPagamentoSimulado>());
        }
        else
        {
            services.AddHttpClient<IProvedorPagamento, ProvedorMercadoPago>(http => ProvedorMercadoPago.Configurar(http, opcoes));
        }

        if (opcoes.ReconciliacaoHabilitada)
            services.AddHostedService<ReconciliacaoPagamentosHostedService>();
    }

    // Consumidor dos comandos da Saga e despachante da outbox (ADR-016, ADR-018). Desabilitado, nenhuma conexão é aberta.
    public static IServiceCollection AddMensageria(this IServiceCollection services, IConfiguration configuration)
    {
        var secao = configuration.GetSection(RabbitMqOptions.Secao);
        var opcoes = secao.Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        if (!opcoes.Enabled)
            return services;

        // Conexão autenticada: sem usuário e senha configurados o serviço não sobe (não usa guest/guest implícito).
        if (string.IsNullOrWhiteSpace(opcoes.Username) || string.IsNullOrWhiteSpace(opcoes.Password))
            throw new InvalidOperationException("RabbitMq:Username e RabbitMq:Password são obrigatórios quando RabbitMq:Enabled=true.");

        services.Configure<RabbitMqOptions>(secao);
        services.AddSingleton<EstadoConsumidorSaga>();
        services.AddHostedService<ConsumidorSagaHostedService>();
        services.AddSingleton<PublicadorRabbitMq>();
        services.AddHostedService<DespachanteOutboxHostedService>();
        return services;
    }
}
