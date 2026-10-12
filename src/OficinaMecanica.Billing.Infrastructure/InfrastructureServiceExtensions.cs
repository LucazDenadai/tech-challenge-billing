using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Application.Ports.In;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
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

        return services;
    }
}
