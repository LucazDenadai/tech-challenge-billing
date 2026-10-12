namespace OficinaMecanica.Billing.Domain.Comum;

public abstract class EntityBase
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTime CriadoEm { get; protected set; } = DateTime.UtcNow;
    public DateTime? AtualizadoEm { get; protected set; }

    public void MarcarAtualizado() => AtualizadoEm = DateTime.UtcNow;
}
