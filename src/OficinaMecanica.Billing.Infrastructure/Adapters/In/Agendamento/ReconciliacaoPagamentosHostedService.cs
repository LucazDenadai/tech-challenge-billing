using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.In.Agendamento;

// Reconciliação dos pagamentos não terminais (ADR-019): refaz criações sem resposta, consulta Pix pendentes
// sem notificação e confirma estornos. Funciona mesmo sem webhook (por exemplo, antes de existir URL pública).
// Com mais de uma réplica, duas podem ler o mesmo lote; o xmin deixa só uma gravar e a chave idempotente
// impede segunda cobrança no provedor.
public class ReconciliacaoPagamentosHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<MercadoPagoOptions> options,
    ILogger<ReconciliacaoPagamentosHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromSeconds(options.Value.IntervaloReconciliacaoSegundos);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var escopo = scopeFactory.CreateScope();
                var verificar = escopo.ServiceProvider.GetRequiredService<VerificarPagamentosUseCase>();
                if (await verificar.ExecutarAsync(stoppingToken) == VerificarPagamentosUseCase.Lote)
                    continue; // lote cheio: pode haver mais a verificar
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Falha na reconciliação de pagamentos; nova tentativa no próximo intervalo.");
            }

            await Task.Delay(intervalo, stoppingToken);
        }
    }
}
