using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Orcamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Mensageria;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;

// PostgreSQL dedicado de Billing (ADR-016): orçamento, inbox e outbox. Pagamento entra no CARD-39b.
public class AppDbContext : DbContext
{
    [ExcludeFromCodeCoverage]
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    [ExcludeFromCodeCoverage] public DbSet<MensagemInbox> Inbox => Set<MensagemInbox>();
    public DbSet<MensagemOutbox> Outbox => Set<MensagemOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
