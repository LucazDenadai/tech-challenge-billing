using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Orcamentos;

// Efeito do QuoteRequested: cria o orçamento ou a próxima versão e devolve o evento de resultado.
// Não grava: quem chama inclui o evento na outbox e grava tudo em uma transação (IUnidadeDeTrabalho).
// O valor sai só do snapshot recebido; Billing não consulta Operações (ADR-017).
public class SolicitarOrcamentoUseCase(IOrcamentoRepository repository, ConfiguracaoOrcamento configuracao, TimeProvider relogio)
{
    public async Task<MensagemSaga> ExecutarAsync(QuoteRequested pedido, CancellationToken ct = default)
    {
        var origem = OrigemEvento.De(pedido);
        var agora = relogio.GetUtcNow();
        var chave = pedido.IdempotencyKey!;

        try
        {
            var moeda = pedido.Currency ?? throw new OrcamentoInvalidoException("O pedido não informa a moeda.");
            var itens = pedido.Items
                .Select(i => new ItemPrecificado(i.ItemId, Enum.Parse<TipoItemOrcamento>(i.Type), i.Description, i.Quantity, i.UnitPrice))
                .ToList();

            var existente = await repository.ObterPorOsAsync(pedido.OsId, ct);
            if (existente is null)
            {
                var orcamento = Orcamento.Criar(pedido.OsId, pedido.ClienteId, pedido.FilialId, pedido.CorrelationId, chave,
                    moeda, pedido.PriceSnapshotAtUtc, itens, agora, configuracao.Validade);
                await repository.AdicionarAsync(orcamento, ct);
                return RespostaSaga.Pronto(origem, orcamento.Id, orcamento.VersaoVigente);
            }

            // Mesmo pedido de novo (outra mensagem, mesma chave): devolve a versão já criada.
            if (existente.VersaoPorIdempotencyKey(chave) is { } mesma)
                return RespostaSaga.Pronto(origem, existente.Id, mesma);

            if (existente.ClienteId != pedido.ClienteId)
                return RespostaSaga.Recusado(origem, "O cliente do pedido difere do cliente do orçamento existente da OS.");

            var nova = existente.NovaVersao(chave, moeda, pedido.PriceSnapshotAtUtc, itens, agora, configuracao.Validade);
            return RespostaSaga.Pronto(origem, existente.Id, nova);
        }
        catch (OrcamentoInvalidoException ex)
        {
            return RespostaSaga.Recusado(origem, ex.Message);
        }
    }
}
