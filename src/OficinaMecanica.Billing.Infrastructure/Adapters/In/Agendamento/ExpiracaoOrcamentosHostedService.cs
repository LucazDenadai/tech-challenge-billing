using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.In.Agendamento;

public class ExpiracaoOptions
{
    public const string Secao = "Orcamento";

    public bool ExpiracaoHabilitada { get; set; } = true;
    public int IntervaloExpiracaoSegundos { get; set; } = 60;
}

// Procura, a cada intervalo, versões de orçamento vencidas sem decisão e as expira (QuoteExpired, ADR-017).
// Não depende do broker: o evento vai para a outbox e o despachante publica quando houver conexão.
// Com mais de uma réplica, duas podem ler o mesmo lote; a concorrência pelo xmin deixa só uma gravar.
public class ExpiracaoOrcamentosHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ExpiracaoOptions> options,
    ILogger<ExpiracaoOrcamentosHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromSeconds(options.Value.IntervaloExpiracaoSegundos);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var escopo = scopeFactory.CreateScope();
                var expirar = escopo.ServiceProvider.GetRequiredService<ExpirarOrcamentosUseCase>();
                if (await expirar.ExecutarAsync(stoppingToken) == ExpirarOrcamentosUseCase.Lote)
                    continue; // lote cheio: pode haver mais vencidos
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Falha ao expirar orçamentos; nova tentativa no próximo intervalo.");
            }

            await Task.Delay(intervalo, stoppingToken);
        }
    }
}
