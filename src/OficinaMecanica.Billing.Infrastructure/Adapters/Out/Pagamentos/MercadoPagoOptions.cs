namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

public enum ModoProvedor
{
    // Provedor em memória: local, testes, BDD e vídeo, sem credenciais (ADR-019).
    Simulado,
    // API do Mercado Pago. Sandbox ou produção é decidido pelas credenciais.
    MercadoPago
}

// Seção MercadoPago. AccessToken e WebhookSecret vêm de variável de ambiente ou Secret (CARD-41), nunca do appsettings
// versionado, e nunca entram em log.
public class MercadoPagoOptions
{
    public const string Secao = "MercadoPago";

    public ModoProvedor Modo { get; set; } = ModoProvedor.Simulado;
    public string BaseUrl { get; set; } = "https://api.mercadopago.com";
    public string AccessToken { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    // URL pública do webhook enviada na criação (notification_url). Vazia: vale a URL configurada no painel.
    public string UrlNotificacao { get; set; } = string.Empty;

    public int ExpiracaoMinutos { get; set; } = 30;
    public int TimeoutSegundos { get; set; } = 10;
    // Tentativas imediatas por chamada para timeout, rede, 5xx e 429; depois o resultado fica desconhecido.
    public int TentativasPorChamada { get; set; } = 3;
    public int ToleranciaAssinaturaMinutos { get; set; } = 30;

    public bool ReconciliacaoHabilitada { get; set; } = true;
    public int IntervaloReconciliacaoSegundos { get; set; } = 60;
    public int IntervaloVerificacaoMinutos { get; set; } = 2;
    public int MaxTentativasReconciliacao { get; set; } = 10;

    public void Validar()
    {
        if (ExpiracaoMinutos < 30)
            throw new InvalidOperationException("MercadoPago:ExpiracaoMinutos deve ser ao menos 30 (mínimo do Pix).");
        if (Modo == ModoProvedor.MercadoPago && (string.IsNullOrWhiteSpace(AccessToken) || string.IsNullOrWhiteSpace(WebhookSecret)))
            throw new InvalidOperationException("MercadoPago:AccessToken e MercadoPago:WebhookSecret são obrigatórios quando MercadoPago:Modo=MercadoPago.");
        if (TimeoutSegundos <= 0 || TentativasPorChamada <= 0 || MaxTentativasReconciliacao <= 0
            || IntervaloReconciliacaoSegundos <= 0 || IntervaloVerificacaoMinutos <= 0)
            throw new InvalidOperationException("Os tempos e limites da seção MercadoPago devem ser maiores que zero.");
    }
}
