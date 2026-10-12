using System.Text;
using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;

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
public class ProcessarMensagemSagaUseCase(
    ITransacao transacao,
    IInboxRepository inbox,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    SolicitarOrcamentoUseCase solicitarOrcamento,
    ILogger<ProcessarMensagemSagaUseCase> logger)
{
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
            var status = await transacao.ExecutarAsync(async () =>
            {
                if (!await inbox.RegistrarAsync(mensagem, canal, payload, ct))
                    return StatusProcessamento.Duplicada;

                outbox.Adicionar(await AplicarAsync(mensagem, ct));
                await unidadeDeTrabalho.SalvarAsync(ct);
                return StatusProcessamento.Processada;
            }, ct);

            logger.LogInformation("Mensagem {Status}. MessageType={MessageType} OsId={OsId}", status, mensagem.MessageType, mensagem.OsId);
            return new ResultadoProcessamento(status, null, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }
        catch (NotFoundException ex)
        {
            // Referência inexistente não se resolve com nova tentativa: vai para a DLQ com o motivo.
            return new ResultadoProcessamento(StatusProcessamento.Rejeitada, ex.Message, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }
    }

    private async Task<MensagemSaga> AplicarAsync(MensagemSaga mensagem, CancellationToken ct)
        => mensagem switch
        {
            QuoteRequested pedido => await solicitarOrcamento.ExecutarAsync(pedido, ct),
            _ => throw new InvalidOperationException($"Sem tratamento para {mensagem.MessageType}.")
        };
}
