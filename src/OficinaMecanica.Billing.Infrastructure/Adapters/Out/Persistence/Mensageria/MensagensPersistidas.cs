using System.Diagnostics.CodeAnalysis;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Mensageria;

// Registro de mensagem recebida, chave de deduplicação por messageId (ADR-017).
// Modela a tabela para as migrations; a gravação é feita por INSERT ... ON CONFLICT no InboxRepository.
[ExcludeFromCodeCoverage]
public class MensagemInbox
{
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Canal { get; set; } = string.Empty;
    public string Producer { get; set; } = string.Empty;
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public Guid OsId { get; set; }
    public Guid FilialId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset RecebidaEmUtc { get; set; }
    public string Payload { get; set; } = string.Empty;
}

// Evento a publicar, gravado na mesma transação do efeito (ADR-016). O despachante publica e marca.
public class MensagemOutbox
{
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Canal { get; set; } = string.Empty;
    public Guid CorrelationId { get; set; }
    public string Payload { get; set; } = string.Empty;
    // Contexto W3C do trace que gerou o evento, para o consumidor continuar o mesmo trace.
    public string? TraceParent { get; set; }
    public DateTimeOffset CriadaEmUtc { get; set; }
    public DateTimeOffset? PublicadaEmUtc { get; set; }
    public int Tentativas { get; set; }
    public string? UltimoErro { get; set; }
}
