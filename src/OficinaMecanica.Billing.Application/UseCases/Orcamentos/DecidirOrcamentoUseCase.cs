using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Orcamentos;

public sealed record DecidirOrcamentoCommand(Guid QuoteId, int Versao, ResultadoDecisao Resultado, Guid ClienteId, string? Motivo);

public sealed record ResultadoDecisaoOrcamento(StatusTentativaDecisao Status, OrcamentoResponse Orcamento);

// Aprovação ou recusa pelo cliente dono da OS, via REST (ADR-017). A decisão e o evento QuoteApproved/QuoteDeclined
// são gravados juntos; o OS segue a Saga a partir do evento. Tentativa negada não muda estado nem histórico.
public class DecidirOrcamentoUseCase(
    IOrcamentoRepository repository,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    TimeProvider relogio,
    ILogger<DecidirOrcamentoUseCase> logger)
{
    // Duas decisões simultâneas: a segunda recebe conflito, relê e responde conforme o que ficou gravado.
    private const int Tentativas = 3;

    public async Task<ResultadoDecisaoOrcamento> ExecutarAsync(DecidirOrcamentoCommand comando, CancellationToken ct = default)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            var orcamento = await repository.ObterPorIdAsync(comando.QuoteId, ct);

            // Orçamento de outro cliente responde como inexistente, para não revelar que existe.
            if (orcamento is null || orcamento.ClienteId != comando.ClienteId)
                throw new NotFoundException("Orçamento", comando.QuoteId);

            var agora = relogio.GetUtcNow();
            var resultado = orcamento.Decidir(comando.Versao, comando.Resultado, comando.ClienteId, comando.Motivo, agora);

            if (resultado.Status == StatusTentativaDecisao.Registrada)
                outbox.Adicionar(RespostaSaga.Decidido(orcamento, resultado.Versao!, agora));
            if (resultado.ExpirouAgora)
                outbox.Adicionar(RespostaSaga.Expirado(orcamento, resultado.Versao!));

            if (resultado.Status == StatusTentativaDecisao.Registrada || resultado.ExpirouAgora)
            {
                try
                {
                    await unidadeDeTrabalho.SalvarAsync(ct);
                }
                catch (ConflitoConcorrenciaException) when (tentativa < Tentativas)
                {
                    unidadeDeTrabalho.DescartarAlteracoes();
                    continue;
                }
            }

            logger.LogInformation("Decisão de orçamento: {Status}. QuoteId={QuoteId} Versao={Versao} Resultado={Resultado}",
                resultado.Status, comando.QuoteId, comando.Versao, comando.Resultado);
            return new ResultadoDecisaoOrcamento(resultado.Status, OrcamentoResponse.De(orcamento));
        }
    }
}
