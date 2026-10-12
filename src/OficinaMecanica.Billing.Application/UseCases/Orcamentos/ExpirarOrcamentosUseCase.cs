using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Ports.Out;

namespace OficinaMecanica.Billing.Application.UseCases.Orcamentos;

// Expira as versões que venceram sem decisão e publica QuoteExpired; o OS encerra a Saga sem reserva nem cobrança (ADR-017).
// Um lote por chamada, gravado em uma transação. Se uma decisão concorrente gravar antes, o lote falha com conflito
// e a próxima rodada relê: a versão decidida não aparece mais como vencida.
public class ExpirarOrcamentosUseCase(
    IOrcamentoRepository repository,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    TimeProvider relogio,
    ILogger<ExpirarOrcamentosUseCase> logger)
{
    public const int Lote = 50;

    // Retorna quantas versões expiraram.
    public async Task<int> ExecutarAsync(CancellationToken ct = default)
    {
        var agora = relogio.GetUtcNow();
        var vencidos = await repository.ListarComVersaoVencidaAsync(agora, Lote, ct);

        var expirados = 0;
        foreach (var orcamento in vencidos)
        {
            if (orcamento.ExpirarSeVencido(agora) is not { } versao)
                continue;

            outbox.Adicionar(RespostaSaga.Expirado(orcamento, versao));
            expirados++;
        }

        if (expirados > 0)
        {
            await unidadeDeTrabalho.SalvarAsync(ct);
            logger.LogInformation("{Quantidade} versões de orçamento expiradas.", expirados);
        }

        return expirados;
    }
}
