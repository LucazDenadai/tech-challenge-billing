using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.Ports.Out;

public interface IPagamentoRepository : IRepository<Pagamento>
{
    Task<Pagamento?> ObterPorIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    Task<Pagamento?> ObterPorOrcamentoAsync(Guid quoteId, int quoteVersion, CancellationToken ct = default);

    Task<IReadOnlyList<Pagamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default);

    Task<IReadOnlyList<Pagamento>> ListarPorOsAsync(Guid osId, CancellationToken ct = default);

    // Ids dos pagamentos cuja verificação venceu, mais antigos primeiro (reconciliação em segundo plano).
    Task<IReadOnlyList<Guid>> ListarParaVerificarAsync(DateTimeOffset agora, int limite, CancellationToken ct = default);
}

public interface INotificacaoPagamentoRepository
{
    Task<bool> ExisteAsync(string requestId, CancellationToken ct = default);

    Task AdicionarAsync(NotificacaoPagamento notificacao, CancellationToken ct = default);
}
