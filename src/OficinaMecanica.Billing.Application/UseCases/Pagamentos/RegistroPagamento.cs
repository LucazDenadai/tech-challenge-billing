using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

// Grava a mudança do pagamento e o evento que ela exige na mesma transação (outbox, ADR-016).
// Conflito de concorrência: outro processo (webhook, reconciliação ou consumidor) gravou antes. A mudança é
// descartada e a próxima verificação relê o pagamento; nenhum evento sai em dobro.
internal sealed class RegistroPagamento(IOutbox outbox, IUnidadeDeTrabalho unidadeDeTrabalho, ILogger logger)
{
    public async Task<bool> GravarAsync(Pagamento pagamento, EventoPagamento? evento, bool estavaEmAcaoManual, CancellationToken ct)
    {
        if (evento is { } e)
            outbox.Adicionar(RespostaSaga.DoPagamento(pagamento, e));

        try
        {
            await unidadeDeTrabalho.SalvarAsync(ct);
        }
        catch (ConflitoConcorrenciaException)
        {
            unidadeDeTrabalho.DescartarAlteracoes();
            logger.LogInformation("Pagamento {PagamentoId} alterado por outro processo; a próxima verificação relê.", pagamento.Id);
            return false;
        }

        if (evento is { } publicado)
            logger.LogInformation("Pagamento {PagamentoId} em {Status}; evento {Evento} na outbox.", pagamento.Id, pagamento.Status, publicado);

        if (!estavaEmAcaoManual && pagamento.ReconciliacaoManual)
        {
            MetricasPagamento.AcaoManual();
            logger.LogError("Pagamento {PagamentoId} aguarda ação manual: {Motivo}", pagamento.Id, pagamento.MotivoReconciliacaoManual);
        }

        if (evento == EventoPagamento.EstornoFalhou)
            logger.LogError("Estorno do pagamento {PagamentoId} falhou: {Motivo}", pagamento.Id, pagamento.MotivoFalhaEstorno);

        return true;
    }

    // Consulta autoritativa feita pela própria Billing (criação ou reconciliação): divergência não se resolve sozinha.
    public static EventoPagamento? Reconciliar(Pagamento pagamento, PagamentoNoProvedor noProvedor, DateTimeOffset agora,
        ConfiguracaoPagamento configuracao)
    {
        var reconciliacao = pagamento.Reconciliar(noProvedor, agora, configuracao.IntervaloVerificacao);
        if (reconciliacao.Resultado == ResultadoReconciliacao.Divergente)
            pagamento.MarcarParaAcaoManual(reconciliacao.Detalhe!);
        return reconciliacao.Evento;
    }
}
