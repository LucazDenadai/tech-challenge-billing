using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Configurations;

public class OrcamentoConfiguration : IEntityTypeConfiguration<Orcamento>
{
    // Coluna de sistema xmin do PostgreSQL como token de concorrência. Toda mudança no agregado
    // (versão nova, decisão, expiração) atualiza a linha do orçamento, então duas gravações sobre a
    // mesma leitura não passam: a segunda recebe conflito e refaz a operação.
    public const string Versao = "Versao";

    public void Configure(EntityTypeBuilder<Orcamento> builder)
    {
        builder.ToTable("Orcamentos");
        builder.HasKey(o => o.Id);
        // Ids gerados no domínio: entidades novas nas coleções são inseridas, não atualizadas.
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.HasIndex(o => o.OsId).IsUnique();
        builder.HasIndex(o => o.ClienteId);
        builder.HasIndex(o => o.CorrelationId);
        builder.Property<uint>(Versao).IsRowVersion();
        builder.Ignore(o => o.VersaoVigente);

        builder.HasMany(o => o.Versoes).WithOne().HasForeignKey(v => v.OrcamentoId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Versoes).HasField("_versoes").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(o => o.Decisoes).WithOne().HasForeignKey(d => d.OrcamentoId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Decisoes).HasField("_decisoes").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class VersaoOrcamentoConfiguration : IEntityTypeConfiguration<VersaoOrcamento>
{
    public void Configure(EntityTypeBuilder<VersaoOrcamento> builder)
    {
        builder.ToTable("VersoesOrcamento", t => t.HasCheckConstraint("CK_VersoesOrcamento_ValorTotal", "\"ValorTotal\" > 0"));
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.HasIndex(v => new { v.OrcamentoId, v.Numero }).IsUnique();
        builder.HasIndex(v => new { v.OrcamentoId, v.IdempotencyKey }).IsUnique();
        builder.Property(v => v.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(v => v.Moeda).HasMaxLength(3).IsRequired();
        builder.Property(v => v.ValorTotal).HasPrecision(12, 2);
        builder.Property(v => v.Status).IsRequired();
        builder.Ignore(v => v.AguardandoDecisao);
        // O serviço de expiração procura as versões pendentes vencidas.
        builder.HasIndex(v => v.ExpiraEm).HasFilter($"\"Status\" = {(int)StatusVersaoOrcamento.AguardandoDecisao}");

        builder.HasMany(v => v.Itens).WithOne().HasForeignKey(i => i.VersaoId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(v => v.Itens).HasField("_itens").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class ItemOrcamentoConfiguration : IEntityTypeConfiguration<ItemOrcamento>
{
    public void Configure(EntityTypeBuilder<ItemOrcamento> builder)
    {
        builder.ToTable("ItensOrcamento", t =>
        {
            t.HasCheckConstraint("CK_ItensOrcamento_Quantidade", "\"Quantidade\" >= 1");
            t.HasCheckConstraint("CK_ItensOrcamento_Preco", "\"PrecoUnitario\" > 0");
        });
        builder.HasKey(i => new { i.VersaoId, i.Sequencia });
        builder.Property(i => i.Descricao).HasMaxLength(200).IsRequired();
        builder.Property(i => i.PrecoUnitario).HasPrecision(12, 2);
        builder.Property(i => i.Tipo).IsRequired();
        builder.Ignore(i => i.Subtotal);
    }
}

public class DecisaoOrcamentoConfiguration : IEntityTypeConfiguration<DecisaoOrcamento>
{
    public void Configure(EntityTypeBuilder<DecisaoOrcamento> builder)
    {
        builder.ToTable("DecisoesOrcamento");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        // Uma decisão por versão: a decisão repetida não grava nova linha.
        builder.HasIndex(d => new { d.OrcamentoId, d.Versao }).IsUnique();
        builder.Property(d => d.Resultado).IsRequired();
        builder.Property(d => d.Motivo).HasMaxLength(500);
    }
}
