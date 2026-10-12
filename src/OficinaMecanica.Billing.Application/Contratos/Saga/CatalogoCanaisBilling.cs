namespace OficinaMecanica.Billing.Application.Contratos.Saga;

public sealed record CanalConsumido(string Endereco, string MessageType, int SchemaVersion, string Produtor, Type TipoMensagem);

public sealed record CanalPublicado(string Endereco, string MessageType, Type TipoMensagem);

// Canais de Billing no AsyncAPI (ADR-018). Testes de contrato comparam estas listas com a spec.
// Orçamento (CARD-39a); pagamento e estorno entram no CARD-39b.
public static class CatalogoCanaisBilling
{
    public const string Produtor = "billing";

    public static readonly IReadOnlyList<CanalConsumido> Consumidos =
    [
        Consumido<QuoteRequested>("saga-os.quote-requested.v1")
    ];

    public static readonly IReadOnlyList<CanalPublicado> Publicados =
    [
        Publicado<QuoteReady>("saga-os.quote-ready.v1"),
        Publicado<QuoteRejected>("saga-os.quote-rejected.v1"),
        Publicado<QuoteApproved>("saga-os.quote-approved.v1"),
        Publicado<QuoteDeclined>("saga-os.quote-declined.v1"),
        Publicado<QuoteExpired>("saga-os.quote-expired.v1")
    ];

    public static CanalConsumido? ObterConsumido(string endereco) => Consumidos.FirstOrDefault(c => c.Endereco == endereco);

    public static CanalPublicado Publicado(Type tipo)
        => Publicados.FirstOrDefault(c => c.TipoMensagem == tipo)
           ?? throw new InvalidOperationException($"Billing não publica {tipo.Name}.");

    // Todos os canais são v1: o messageType é o nome do DTO + ".v1", como `name` das mensagens na spec.
    private static CanalConsumido Consumido<T>(string endereco) where T : MensagemSaga
        => new(endereco, $"{typeof(T).Name}.v1", 1, "os", typeof(T));

    private static CanalPublicado Publicado<T>(string endereco) where T : MensagemSaga
        => new(endereco, $"{typeof(T).Name}.v1", typeof(T));
}
