using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Configurations;

public class PagamentoConfiguration : IEntityTypeConfiguration<Pagamento>
{
    public void Configure(EntityTypeBuilder<Pagamento> builder)
    {
        builder.ToTable("Pagamentos", t => t.HasCheckConstraint("CK_Pagamentos_Valor", "\"Valor\" > 0"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        // Mesmo token de concorrência do orçamento: webhook, reconciliação e consumidor podem mexer no mesmo pagamento;
        // só um grava e o outro relê.
        builder.Property<uint>(OrcamentoConfiguration.Versao).IsRowVersion();

        // Um pagamento por comando e por versão de orçamento: nunca duas cobranças para a mesma coisa.
        builder.HasIndex(p => p.IdempotencyKey).IsUnique();
        builder.HasIndex(p => new { p.QuoteId, p.QuoteVersion }).IsUnique();
        builder.HasIndex(p => p.ReferenciaProvedor).IsUnique().HasFilter("\"ReferenciaProvedor\" IS NOT NULL");
        builder.HasIndex(p => p.ClienteId);
        builder.HasIndex(p => p.OsId);
        builder.HasIndex(p => p.CorrelationId);
        // A reconciliação procura só o que tem verificação agendada e não parou em ação manual.
        builder.HasIndex(p => p.ProximaVerificacaoEm)
            .HasFilter("\"ProximaVerificacaoEm\" IS NOT NULL AND NOT \"ReconciliacaoManual\"");

        builder.Property(p => p.Valor).HasPrecision(12, 2);
        builder.Property(p => p.Moeda).HasMaxLength(3).IsRequired();
        builder.Property(p => p.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(p => p.EmailPagador).HasMaxLength(254);
        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.ReferenciaProvedor).HasMaxLength(50);
        builder.Property(p => p.StatusProvedor).HasMaxLength(50);
        builder.Property(p => p.CodigoPix).HasMaxLength(2000);
        builder.Property(p => p.UrlPagamento).HasMaxLength(500);
        builder.Property(p => p.MotivoRecusa).HasMaxLength(500);
        builder.Property(p => p.EstornoIdempotencyKey).HasMaxLength(200);
        builder.Property(p => p.MotivoEstorno).HasMaxLength(500);
        builder.Property(p => p.ReferenciaEstorno).HasMaxLength(50);
        builder.Property(p => p.MotivoFalhaEstorno).HasMaxLength(500);
        builder.Property(p => p.MotivoReconciliacaoManual).HasMaxLength(500);

        builder.Ignore(p => p.PrecisaCriarNoProvedor);
        builder.Ignore(p => p.PrecisaPedirEstorno);
        builder.Ignore(p => p.EventoDoEstornoAtual);
    }
}

public class NotificacaoPagamentoConfiguration : IEntityTypeConfiguration<NotificacaoPagamento>
{
    public void Configure(EntityTypeBuilder<NotificacaoPagamento> builder)
    {
        builder.ToTable("NotificacoesPagamento");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        // Replay da mesma requisição do provedor esbarra no índice único e não é processado de novo.
        builder.HasIndex(n => n.RequestId).IsUnique();
        builder.HasIndex(n => n.PagamentoId);
        builder.Property(n => n.RequestId).HasMaxLength(NotificacaoPagamento.TamanhoMaximoRequestId).IsRequired();
        builder.Property(n => n.ReferenciaRecurso).HasMaxLength(100).IsRequired();
        builder.Property(n => n.Acao).HasMaxLength(100);
        builder.Property(n => n.Detalhe).HasMaxLength(500);
        builder.Property(n => n.Resultado).IsRequired();
    }
}
