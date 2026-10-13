using System.Text;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Mensageria;

public enum StatusProcessamento
{
    Processada,
    Duplicada,
    Rejeitada
}

public sealed record ResultadoProcessamento(StatusProcessamento Status, string? Motivo, Guid? MessageId, Guid? CorrelationId, string? MessageType);

// Participante da Saga: valida o comando contra o contrato do canal e, em uma única transação do PostgreSQL
// de Billing, registra a inbox, aplica o efeito e grava o evento de resultado na outbox (ADR-016).
// Conflito de concorrência e falha de infraestrutura sobem como exceção: o consumidor refaz a mensagem.
// Chamadas ao Mercado Pago nunca ficam dentro da transação: rodam depois do commit (ADR-019).
public class ProcessarMensagemSagaUseCase(
    ITransacao transacao,
    IInboxRepository inbox,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    SolicitarOrcamentoUseCase solicitarOrcamento,
    SolicitarPagamentoUseCase solicitarPagamento,
    SolicitarEstornoUseCase solicitarEstorno,
    CobrarPagamentoUseCase cobrar,
    EstornarPagamentoUseCase estornar,
    ILogger<ProcessarMensagemSagaUseCase> logger)
{
    // Evento de resposta (opcional) e o que fazer depois do commit, como chamar o provedor.
    private sealed record Efeito(MensagemSaga? Evento, Func<CancellationToken, Task>? AposConfirmar = null);

    public async Task<ResultadoProcessamento> ExecutarAsync(string canal, ReadOnlyMemory<byte> corpo, CancellationToken ct = default)
    {
        var contrato = CatalogoCanaisBilling.ObterConsumido(canal);
        if (contrato is null)
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, $"Canal '{canal}' não é consumido por Billing.", null, null, null);

        var leitura = LeitorMensagemSaga.Ler(contrato, corpo);
        if (!leitura.Valida)
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, leitura.Motivo, leitura.MessageId, leitura.CorrelationId, leitura.MessageType);

        var mensagem = leitura.Mensagem!;
        try
        {
            var payload = Encoding.UTF8.GetString(corpo.Span);
            Efeito? efeito = null;
            var status = await transacao.ExecutarAsync(async () =>
            {
                if (!await inbox.RegistrarAsync(mensagem, canal, payload, ct))
                    return StatusProcessamento.Duplicada;

                efeito = await AplicarAsync(mensagem, ct);
                if (efeito.Evento is { } evento)
                    outbox.Adicionar(evento);
                await unidadeDeTrabalho.SalvarAsync(ct);
                return StatusProcessamento.Processada;
            }, ct);

            logger.LogInformation("Mensagem {Status}. MessageType={MessageType} OsId={OsId}", status, mensagem.MessageType, mensagem.OsId);
            if (status == StatusProcessamento.Processada && efeito?.AposConfirmar is { } aposConfirmar)
                await ExecutarAposConfirmarAsync(aposConfirmar, mensagem, ct);
            return new ResultadoProcessamento(status, null, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }
        catch (Exception ex) when (ex is NotFoundException or PagamentoInvalidoException)
        {
            // Referência inexistente ou comando que não pode gerar cobrança/estorno não se resolve com nova tentativa:
            // vai para a DLQ com o motivo, sem nenhuma chamada ao provedor.
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, ex.Message, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }
    }

    // O comando já está confirmado. Falha aqui não volta a mensagem para a fila (a inbox a trataria como duplicada):
    // o pagamento continua com verificação agendada e a reconciliação em segundo plano refaz a chamada.
    private async Task ExecutarAposConfirmarAsync(Func<CancellationToken, Task> acao, MensagemSaga mensagem, CancellationToken ct)
    {
        try
        {
            await acao(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chamada ao provedor após {MessageType} falhou; a reconciliação tenta de novo. OsId={OsId}",
                mensagem.MessageType, mensagem.OsId);
        }
    }

    private async Task<Efeito> AplicarAsync(MensagemSaga mensagem, CancellationToken ct)
    {
        switch (mensagem)
        {
            case QuoteRequested pedido:
                return new Efeito(await solicitarOrcamento.ExecutarAsync(pedido, ct));

            case PaymentCreationRequested pedido:
                var solicitado = await solicitarPagamento.ExecutarAsync(pedido, ct);
                var pagamentoId = solicitado.Pagamento.Id;
                return new Efeito(solicitado.EventoRepetido,
                    solicitado.Pagamento.PrecisaCriarNoProvedor ? c => cobrar.ExecutarAsync(pagamentoId, buscarAntes: false, c) : null);

            case PaymentRefundRequested pedido:
                var estorno = await solicitarEstorno.ExecutarAsync(pedido, ct);
                var estornoId = estorno.Pagamento.Id;
                return new Efeito(estorno.EventoRepetido,
                    estorno.Pagamento.PrecisaPedirEstorno ? c => estornar.ExecutarAsync(estornoId, c) : null);

            default:
                throw new InvalidOperationException($"Sem tratamento para {mensagem.MessageType}.");
        }
    }
}
