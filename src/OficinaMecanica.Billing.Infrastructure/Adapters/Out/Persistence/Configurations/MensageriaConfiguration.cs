using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Mensageria;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Configurations;

public class MensagemInboxConfiguration : IEntityTypeConfiguration<MensagemInbox>
{
    public void Configure(EntityTypeBuilder<MensagemInbox> builder)
    {
        builder.ToTable("InboxMensagens");
        builder.HasKey(m => m.MessageId);
        builder.Property(m => m.MessageId).ValueGeneratedNever();
        builder.Property(m => m.MessageType).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Canal).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Producer).HasMaxLength(20).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(m => m.CorrelationId);
    }
}

public class MensagemOutboxConfiguration : IEntityTypeConfiguration<MensagemOutbox>
{
    public void Configure(EntityTypeBuilder<MensagemOutbox> builder)
    {
        builder.ToTable("OutboxMensagens");
        builder.HasKey(m => m.MessageId);
        builder.Property(m => m.MessageId).ValueGeneratedNever();
        builder.Property(m => m.MessageType).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Canal).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.TraceParent).HasMaxLength(100);
        builder.Property(m => m.UltimoErro).HasMaxLength(500);
        builder.HasIndex(m => m.CorrelationId);
        // O despachante só lê pendentes, em ordem de criação.
        builder.HasIndex(m => m.CriadaEmUtc).HasFilter("\"PublicadaEmUtc\" IS NULL");
    }
}
