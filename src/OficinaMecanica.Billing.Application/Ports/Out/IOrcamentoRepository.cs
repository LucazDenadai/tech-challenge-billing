using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Application.Ports.Out;

// Os métodos de leitura devolvem o agregado completo: versões, itens e decisões.
public interface IOrcamentoRepository : IRepository<Orcamento>
{
    Task<Orcamento?> ObterPorOsAsync(Guid osId, CancellationToken ct = default);

    Task<IReadOnlyList<Orcamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default);

    // Orçamentos cuja versão vigente aguarda decisão e já venceu, mais antigos primeiro.
    Task<IReadOnlyList<Orcamento>> ListarComVersaoVencidaAsync(DateTimeOffset agora, int limite, CancellationToken ct = default);
}
