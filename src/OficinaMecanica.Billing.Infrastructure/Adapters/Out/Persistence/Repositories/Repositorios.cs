using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Application.Exceptions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;

public class UnidadeDeTrabalho(AppDbContext context) : IUnidadeDeTrabalho
{
    public async Task SalvarAsync(CancellationToken ct = default)
    {
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflitoConcorrenciaException(ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Dois pedidos simultâneos criando o mesmo orçamento, pagamento ou notificação: o segundo refaz e encontra o primeiro.
            throw new ConflitoConcorrenciaException(ex);
        }
    }

    public void DescartarAlteracoes() => context.ChangeTracker.Clear();
}

public class OrcamentoRepository(AppDbContext context) : IOrcamentoRepository
{
    private IQueryable<Orcamento> Completos => context.Orcamentos
        .Include(o => o.Versoes).ThenInclude(v => v.Itens)
        .Include(o => o.Decisoes)
        .AsSplitQuery();

    public Task<Orcamento?> ObterPorIdAsync(Guid id, CancellationToken ct = default)
        => Completos.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task AdicionarAsync(Orcamento entidade, CancellationToken ct = default) => await context.Orcamentos.AddAsync(entidade, ct);

    public Task<Orcamento?> ObterPorOsAsync(Guid osId, CancellationToken ct = default)
        => Completos.FirstOrDefaultAsync(o => o.OsId == osId, ct);

    public async Task<IReadOnlyList<Orcamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default)
        => await Completos.Where(o => o.ClienteId == clienteId).OrderByDescending(o => o.CriadoEm).ToListAsync(ct);

    public async Task<IReadOnlyList<Orcamento>> ListarComVersaoVencidaAsync(DateTimeOffset agora, int limite, CancellationToken ct = default)
    {
        var ids = await context.Set<VersaoOrcamento>()
            .Where(v => v.Status == StatusVersaoOrcamento.AguardandoDecisao && v.ExpiraEm <= agora)
            .OrderBy(v => v.ExpiraEm)
            .Select(v => v.OrcamentoId)
            .Take(limite)
            .ToListAsync(ct);

        return await Completos.Where(o => ids.Contains(o.Id)).ToListAsync(ct);
    }
}
