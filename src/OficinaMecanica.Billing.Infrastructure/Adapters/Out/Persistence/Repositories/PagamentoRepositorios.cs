using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;

public class PagamentoRepository(AppDbContext context) : IPagamentoRepository
{
    public Task<Pagamento?> ObterPorIdAsync(Guid id, CancellationToken ct = default)
        => context.Pagamentos.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AdicionarAsync(Pagamento entidade, CancellationToken ct = default) => await context.Pagamentos.AddAsync(entidade, ct);

    public Task<Pagamento?> ObterPorIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => context.Pagamentos.FirstOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, ct);

    public Task<Pagamento?> ObterPorOrcamentoAsync(Guid quoteId, int quoteVersion, CancellationToken ct = default)
        => context.Pagamentos.FirstOrDefaultAsync(p => p.QuoteId == quoteId && p.QuoteVersion == quoteVersion, ct);

    public async Task<IReadOnlyList<Pagamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default)
        => await context.Pagamentos.AsNoTracking().Where(p => p.ClienteId == clienteId).OrderByDescending(p => p.CriadoEm).ToListAsync(ct);

    public async Task<IReadOnlyList<Pagamento>> ListarPorOsAsync(Guid osId, CancellationToken ct = default)
        => await context.Pagamentos.AsNoTracking().Where(p => p.OsId == osId).OrderByDescending(p => p.CriadoEm).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListarParaVerificarAsync(DateTimeOffset agora, int limite, CancellationToken ct = default)
        => await context.Pagamentos
            .Where(p => p.ProximaVerificacaoEm != null && p.ProximaVerificacaoEm <= agora && !p.ReconciliacaoManual)
            .OrderBy(p => p.ProximaVerificacaoEm)
            .Select(p => p.Id)
            .Take(limite)
            .ToListAsync(ct);
}

public class NotificacaoPagamentoRepository(AppDbContext context) : INotificacaoPagamentoRepository
{
    public Task<bool> ExisteAsync(string requestId, CancellationToken ct = default)
        => context.NotificacoesPagamento.AnyAsync(n => n.RequestId == requestId, ct);

    public async Task AdicionarAsync(NotificacaoPagamento notificacao, CancellationToken ct = default)
        => await context.NotificacoesPagamento.AddAsync(notificacao, ct);
}
