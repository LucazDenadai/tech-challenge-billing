using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

// EventoRepetido: o estorno já existia; a resposta é o resultado gravado, sem nova chamada ao provedor.
public sealed record EstornoSolicitado(Pagamento Pagamento, MensagemSaga? EventoRepetido);

// Efeito do PaymentRefundRequested dentro da transação do consumidor (compensação do ADR-017): estorno total.
// A chamada ao provedor acontece depois do commit (EstornarPagamentoUseCase).
public class SolicitarEstornoUseCase(IPagamentoRepository repository, ConfiguracaoPagamento configuracao, TimeProvider relogio)
{
    public async Task<EstornoSolicitado> ExecutarAsync(PaymentRefundRequested pedido, CancellationToken ct = default)
    {
        var pagamento = await repository.ObterPorIdAsync(pedido.PaymentId, ct)
            ?? throw new NotFoundException("Pagamento", pedido.PaymentId);
        if (pagamento.OsId != pedido.OsId)
            throw new PagamentoInvalidoException("O pagamento não pertence à OS do comando.");

        var resultado = pagamento.SolicitarEstorno(pedido.IdempotencyKey!, pedido.MessageId, pedido.Amount, pedido.Currency, pedido.Reason,
            relogio.GetUtcNow(), configuracao.IntervaloVerificacao);

        return resultado == ResultadoPedidoEstorno.JaSolicitado
            ? new EstornoSolicitado(pagamento, RespostaSaga.DoPagamento(pagamento, pagamento.EventoDoEstornoAtual) with { CausationId = pedido.MessageId })
            : new EstornoSolicitado(pagamento, null);
    }
}

// Pede o estorno ao provedor com X-Idempotency-Key = estorno-<paymentId>: o retry nunca gera segundo estorno.
public class EstornarPagamentoUseCase(
    IPagamentoRepository repository,
    IProvedorPagamento provedor,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    ConfiguracaoPagamento configuracao,
    TimeProvider relogio,
    ILogger<EstornarPagamentoUseCase> logger)
{
    public static string ChaveIdempotencia(Guid pagamentoId) => $"estorno-{pagamentoId}";

    public async Task ExecutarAsync(Guid pagamentoId, CancellationToken ct = default)
    {
        var pagamento = await repository.ObterPorIdAsync(pagamentoId, ct);
        if (pagamento is null || !pagamento.PrecisaPedirEstorno)
            return;

        var estavaEmAcaoManual = pagamento.ReconciliacaoManual;
        var resultado = await provedor.EstornarAsync(pagamento.ReferenciaProvedor!, pagamento.Valor, ChaveIdempotencia(pagamento.Id), ct);

        var agora = relogio.GetUtcNow();
        EventoPagamento? evento;
        if (resultado.Ok)
        {
            evento = pagamento.RegistrarEstorno(resultado.Valor!, agora, configuracao.IntervaloVerificacao);
        }
        else
        {
            logger.LogWarning("Estorno do pagamento {PagamentoId} sem resposta do provedor: {Falha}", pagamento.Id, resultado.Falha);
            evento = pagamento.RegistrarFalhaTecnica(agora, configuracao.IntervaloVerificacao, configuracao.MaxTentativas);
        }

        await new RegistroPagamento(outbox, unidadeDeTrabalho, logger).GravarAsync(pagamento, evento, estavaEmAcaoManual, ct);
    }
}
