using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Mensageria;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;

// PostgreSQL dedicado de Billing (ADR-016): orçamento, pagamento, notificações do provedor, inbox e outbox.
public class AppDbContext : DbContext
{
    [ExcludeFromCodeCoverage]
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<NotificacaoPagamento> NotificacoesPagamento => Set<NotificacaoPagamento>();
    [ExcludeFromCodeCoverage] public DbSet<MensagemInbox> Inbox => Set<MensagemInbox>();
    public DbSet<MensagemOutbox> Outbox => Set<MensagemOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
