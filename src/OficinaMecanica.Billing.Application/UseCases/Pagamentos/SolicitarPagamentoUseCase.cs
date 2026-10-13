using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Ports.In;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

// EventoRepetido: resposta a um comando repetido (outra mensagem, mesma idempotencyKey) com o estado já gravado.
public sealed record PagamentoSolicitado(Pagamento Pagamento, MensagemSaga? EventoRepetido);

// Efeito do PaymentCreationRequested dentro da transação do consumidor: registra a tentativa em Solicitado.
// A chamada ao provedor acontece depois do commit (CobrarPagamentoUseCase), nunca dentro da transação.
// Só a versão aprovada do orçamento, com o mesmo valor e moeda, gera cobrança; o resto vai para a DLQ (ADR-019).
public class SolicitarPagamentoUseCase(
    IPagamentoRepository repository,
    IOrcamentoParaPagamento orcamentos,
    ConfiguracaoPagamento configuracao,
    TimeProvider relogio)
{
    public async Task<PagamentoSolicitado> ExecutarAsync(PaymentCreationRequested pedido, CancellationToken ct = default)
    {
        var chave = pedido.IdempotencyKey!;

        if (await repository.ObterPorIdempotencyKeyAsync(chave, ct) is { } existente)
        {
            if (existente.QuoteId != pedido.QuoteId || existente.QuoteVersion != pedido.QuoteVersion)
                throw new PagamentoInvalidoException("A idempotencyKey já foi usada para outro orçamento.");
            return new PagamentoSolicitado(existente, EventoAtual(existente, pedido));
        }

        if (await repository.ObterPorOrcamentoAsync(pedido.QuoteId, pedido.QuoteVersion, ct) is not null)
            throw new PagamentoInvalidoException("Já existe pagamento para esta versão do orçamento, com outra idempotencyKey.");

        var aprovado = await orcamentos.ObterVersaoAprovadaAsync(pedido.QuoteId, pedido.QuoteVersion, ct)
            ?? throw new PagamentoInvalidoException($"A versão {pedido.QuoteVersion} do orçamento não está aprovada.");
        if (aprovado.OsId != pedido.OsId || aprovado.FilialId != pedido.FilialId)
            throw new PagamentoInvalidoException("OS ou filial do comando diferem das do orçamento.");
        if (aprovado.Valor != pedido.Amount || aprovado.Moeda != pedido.Currency)
            throw new PagamentoInvalidoException("Valor ou moeda do comando diferem da versão aprovada do orçamento.");

        var pagamento = Pagamento.Criar(pedido.OsId, pedido.FilialId, pedido.CorrelationId, aprovado.ClienteId, aprovado.QuoteId,
            aprovado.Versao, aprovado.Valor, aprovado.Moeda, chave, pedido.MessageId, pedido.PayerEmail,
            relogio.GetUtcNow(), configuracao.Expiracao, configuracao.IntervaloVerificacao);
        await repository.AdicionarAsync(pagamento, ct);
        return new PagamentoSolicitado(pagamento, null);
    }

    // Mesma referência de pagamento para o mesmo pedido (Gherkin do CARD-39b). Solicitado ainda não tem resposta a dar.
    private static MensagemSaga? EventoAtual(Pagamento pagamento, PaymentCreationRequested pedido)
    {
        EventoPagamento? evento = pagamento.Status switch
        {
            StatusPagamento.Pendente => EventoPagamento.Pendente,
            StatusPagamento.Aprovado => EventoPagamento.Aprovado,
            StatusPagamento.Recusado => EventoPagamento.Recusado,
            _ => null
        };
        return evento is { } e ? RespostaSaga.DoPagamento(pagamento, e) with { CausationId = pedido.MessageId } : null;
    }
}
