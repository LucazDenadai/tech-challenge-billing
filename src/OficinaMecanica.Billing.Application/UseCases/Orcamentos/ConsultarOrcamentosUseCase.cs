using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.In;
using OficinaMecanica.Billing.Application.Ports.Out;

namespace OficinaMecanica.Billing.Application.UseCases.Orcamentos;

public class ConsultarOrcamentosUseCase(IOrcamentoRepository repository) : IOrcamentoParaPagamento
{
    public async Task<IReadOnlyList<OrcamentoResponse>> ListarDoClienteAsync(Guid clienteId, CancellationToken ct = default)
        => (await repository.ListarPorClienteAsync(clienteId, ct)).Select(OrcamentoResponse.De).ToList();

    // clienteId preenchido = consulta do próprio cliente: orçamento de outro cliente responde como inexistente.
    public async Task<OrcamentoResponse> ObterAsync(Guid quoteId, Guid? clienteId, CancellationToken ct = default)
    {
        var orcamento = await repository.ObterPorIdAsync(quoteId, ct);
        if (orcamento is null || (clienteId is { } cliente && orcamento.ClienteId != cliente))
            throw new NotFoundException("Orçamento", quoteId);

        return OrcamentoResponse.De(orcamento);
    }

    public async Task<OrcamentoResponse> ObterPorOsAsync(Guid osId, CancellationToken ct = default)
        => OrcamentoResponse.De(await repository.ObterPorOsAsync(osId, ct) ?? throw new NotFoundException("Orçamento da OS", osId));

    public async Task<OrcamentoAprovado?> ObterVersaoAprovadaAsync(Guid quoteId, int versao, CancellationToken ct = default)
    {
        var orcamento = await repository.ObterPorIdAsync(quoteId, ct);
        return orcamento?.VersaoAprovada(versao) is { } aprovada
            ? new OrcamentoAprovado(orcamento.Id, aprovada.Numero, orcamento.OsId, orcamento.ClienteId, orcamento.FilialId, aprovada.ValorTotal, aprovada.Moeda)
            : null;
    }
}
