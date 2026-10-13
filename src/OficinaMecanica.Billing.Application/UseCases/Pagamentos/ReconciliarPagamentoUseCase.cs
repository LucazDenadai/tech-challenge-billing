using Microsoft.Extensions.Logging;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

// Notificação já autenticada (assinatura validada no adapter HTTP). RequestId = x-request-id.
public sealed record NotificacaoRecebida(string RequestId, string ReferenciaRecurso, string? Acao);

// Reconcilia o pagamento com o que o provedor informa na consulta (ADR-019). O corpo do webhook só diz qual
// recurso consultar; o estado vem do GET no provedor, conferido contra a tentativa gravada.
public class ReconciliarPagamentoUseCase(
    IPagamentoRepository repository,
    INotificacaoPagamentoRepository notificacoes,
    IProvedorPagamento provedor,
    IOutbox outbox,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    ConfiguracaoPagamento configuracao,
    TimeProvider relogio,
    ILogger<ReconciliarPagamentoUseCase> logger)
{
    private const int Tentativas = 3;

    // Devolve null quando a mesma requisição (x-request-id) já foi processada: replay não consulta nem grava de novo.
    public async Task<ResultadoNotificacao?> NotificacaoAsync(NotificacaoRecebida notificacao, CancellationToken ct = default)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            if (await notificacoes.ExisteAsync(notificacao.RequestId, ct))
                return null;

            var consulta = await provedor.ObterAsync(notificacao.ReferenciaRecurso, ct);
            var agora = relogio.GetUtcNow();
            Pagamento? pagamento = null;
            EventoPagamento? evento = null;
            string? detalhe = null;
            ResultadoNotificacao resultado;

            if (!consulta.Ok)
            {
                resultado = ResultadoNotificacao.FalhaTecnica;
                detalhe = consulta.Falha;
            }
            else if (consulta.Valor is null
                     || !Guid.TryParse(consulta.Valor.ReferenciaExterna, out var pagamentoId)
                     || (pagamento = await repository.ObterPorIdAsync(pagamentoId, ct)) is null)
            {
                resultado = ResultadoNotificacao.Desconhecido;
            }
            else
            {
                var reconciliacao = pagamento.Reconciliar(consulta.Valor, agora, configuracao.IntervaloVerificacao);
                evento = reconciliacao.Evento;
                detalhe = reconciliacao.Detalhe;
                resultado = reconciliacao.Resultado switch
                {
                    ResultadoReconciliacao.Transicao => ResultadoNotificacao.Aplicada,
                    ResultadoReconciliacao.Divergente => ResultadoNotificacao.Divergente,
                    ResultadoReconciliacao.ForaDoFluxo => ResultadoNotificacao.ForaDoFluxo,
                    _ => ResultadoNotificacao.SemMudanca
                };
            }

            await notificacoes.AdicionarAsync(new NotificacaoPagamento(notificacao.RequestId, notificacao.ReferenciaRecurso, notificacao.Acao,
                agora, resultado, pagamento?.Id, detalhe), ct);
            if (evento is { } e)
                outbox.Adicionar(RespostaSaga.DoPagamento(pagamento!, e));

            try
            {
                await unidadeDeTrabalho.SalvarAsync(ct);
            }
            catch (ConflitoConcorrenciaException) when (tentativa < Tentativas)
            {
                // Outra réplica gravou a mesma notificação (x-request-id único) ou o mesmo pagamento: relê e decide de novo.
                unidadeDeTrabalho.DescartarAlteracoes();
                continue;
            }

            MetricasPagamento.Notificacao(resultado.ToString());
            if (resultado == ResultadoNotificacao.ForaDoFluxo)
            {
                MetricasPagamento.AcaoManual();
                logger.LogError("Pagamento {PagamentoId} aguarda ação manual: {Motivo}", pagamento!.Id, detalhe);
            }
            if (resultado is ResultadoNotificacao.Desconhecido or ResultadoNotificacao.Divergente)
                logger.LogWarning("Notificação {RequestId} para o recurso {Recurso}: {Resultado}. {Detalhe}",
                    notificacao.RequestId, notificacao.ReferenciaRecurso, resultado, detalhe);
            else
                logger.LogInformation("Notificação {RequestId} para o pagamento {PagamentoId}: {Resultado}.",
                    notificacao.RequestId, pagamento?.Id, resultado);
            return resultado;
        }
    }

    // Verificação periódica de um pagamento já criado no provedor (Pendente ou estorno em processamento).
    public async Task ConsultarAsync(Guid pagamentoId, CancellationToken ct = default)
    {
        var pagamento = await repository.ObterPorIdAsync(pagamentoId, ct);
        if (pagamento?.ReferenciaProvedor is null || pagamento.Status is not (StatusPagamento.Pendente or StatusPagamento.EstornoPendente))
            return;

        var estavaEmAcaoManual = pagamento.ReconciliacaoManual;
        var consulta = await provedor.ObterAsync(pagamento.ReferenciaProvedor, ct);
        var agora = relogio.GetUtcNow();
        EventoPagamento? evento = null;

        if (!consulta.Ok)
            evento = pagamento.RegistrarFalhaTecnica(agora, configuracao.IntervaloVerificacao, configuracao.MaxTentativas);
        else if (consulta.Valor is null)
            pagamento.MarcarParaAcaoManual($"O provedor não encontra o pagamento {pagamento.ReferenciaProvedor}.");
        else
            evento = RegistroPagamento.Reconciliar(pagamento, consulta.Valor, agora, configuracao);

        await new RegistroPagamento(outbox, unidadeDeTrabalho, logger).GravarAsync(pagamento, evento, estavaEmAcaoManual, ct);
    }
}

// Reconciliação em segundo plano (ADR-019): refaz a criação de Solicitado, pede de novo o estorno sem resposta e
// consulta Pendente e EstornoPendente. Cada pagamento é gravado à parte; a falha de um não afeta os outros.
public class VerificarPagamentosUseCase(
    IPagamentoRepository repository,
    CobrarPagamentoUseCase cobrar,
    EstornarPagamentoUseCase estornar,
    ReconciliarPagamentoUseCase reconciliar,
    IUnidadeDeTrabalho unidadeDeTrabalho,
    TimeProvider relogio)
{
    public const int Lote = 20;

    public async Task<int> ExecutarAsync(CancellationToken ct = default)
    {
        var ids = await repository.ListarParaVerificarAsync(relogio.GetUtcNow(), Lote, ct);
        foreach (var id in ids)
        {
            // Relê cada pagamento: outro processo pode ter mudado o estado desde a listagem.
            unidadeDeTrabalho.DescartarAlteracoes();
            var pagamento = await repository.ObterPorIdAsync(id, ct);
            if (pagamento is null)
                continue;

            if (pagamento.PrecisaCriarNoProvedor)
                await cobrar.ExecutarAsync(id, buscarAntes: true, ct);
            else if (pagamento.PrecisaPedirEstorno)
                await estornar.ExecutarAsync(id, ct);
            else
                await reconciliar.ConsultarAsync(id, ct);
        }

        return ids.Count;
    }
}
