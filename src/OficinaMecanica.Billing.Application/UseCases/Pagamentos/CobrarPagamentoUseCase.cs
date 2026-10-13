using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

// Cria a cobrança Pix no provedor para um pagamento Solicitado, fora da transação que o registrou.
// Sucesso: Pendente (ou já aprovado/recusado) e o evento na outbox. Falha técnica: continua Solicitado,
// PaymentOutcomeUnknown na primeira vez e nova tentativa pela reconciliação, com a mesma chave idempotente.
public class CobrarPagamentoUseCase(
    IPagamentoRepository repository,
    IProvedorPagamento provedor,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    ConfiguracaoPagamento configuracao,
    TimeProvider relogio,
    ILogger<CobrarPagamentoUseCase> logger)
{
    // buscarAntes: uma criação anterior pode ter chegado ao provedor sem resposta. Procura pelo external_reference
    // antes de criar de novo, para não depender só de quanto tempo o provedor guarda a chave idempotente.
    public async Task ExecutarAsync(Guid pagamentoId, bool buscarAntes, CancellationToken ct = default)
    {
        var pagamento = await repository.ObterPorIdAsync(pagamentoId, ct);
        if (pagamento is null || !pagamento.PrecisaCriarNoProvedor)
            return;

        var estavaEmAcaoManual = pagamento.ReconciliacaoManual;
        var resultado = buscarAntes
            ? await provedor.BuscarPorPagamentoAsync(pagamento.Id, ct)
            : ResultadoProvedor<PagamentoNoProvedor>.Sucesso(null);

        if (resultado is { Ok: true, Valor: null })
            resultado = await provedor.CriarAsync(new NovaCobranca(pagamento.Id, pagamento.Valor, pagamento.Moeda, pagamento.EmailPagador!,
                $"Ordem de serviço {pagamento.OsId}", pagamento.ExpiraEm, pagamento.Id.ToString()), ct);

        var agora = relogio.GetUtcNow();
        EventoPagamento? evento;
        if (resultado.Ok)
        {
            evento = RegistroPagamento.Reconciliar(pagamento, resultado.Valor!, agora, configuracao);
        }
        else
        {
            logger.LogWarning("Cobrança do pagamento {PagamentoId} sem resposta do provedor: {Falha}", pagamento.Id, resultado.Falha);
            evento = pagamento.RegistrarFalhaTecnica(agora, configuracao.IntervaloVerificacao, configuracao.MaxTentativas);
        }

        await new RegistroPagamento(outbox, unidadeDeTrabalho, logger).GravarAsync(pagamento, evento, estavaEmAcaoManual, ct);
    }
}
