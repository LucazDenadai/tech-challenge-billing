using System.Diagnostics.Metrics;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

// Expiracao: date_of_expiration do Pix (mínimo de 30 minutos no Mercado Pago, ADR-019).
// IntervaloVerificacao: quanto a reconciliação espera para olhar de novo um pagamento não terminal.
// MaxTentativas: falhas técnicas seguidas antes da ação manual.
public sealed record ConfiguracaoPagamento(TimeSpan Expiracao, TimeSpan IntervaloVerificacao, int MaxTentativas);

// Métricas da integração (ADR-019). Exportadas pelo Prometheus junto com as do ASP.NET.
public static class MetricasPagamento
{
    public const string NomeMedidor = "OficinaMecanica.Billing.Pagamentos";

    private static readonly Meter _medidor = new(NomeMedidor);
    private static readonly Counter<long> _chamadas = _medidor.CreateCounter<long>(
        "billing_mercadopago_chamadas", description: "Chamadas ao provedor de pagamento por operação e resultado.");
    private static readonly Counter<long> _notificacoes = _medidor.CreateCounter<long>(
        "billing_webhook_notificacoes", description: "Notificações do webhook por resultado.");
    private static readonly Counter<long> _acaoManual = _medidor.CreateCounter<long>(
        "billing_pagamentos_reconciliacao_manual", description: "Pagamentos que pararam esperando ação manual.");

    public static void Chamada(string operacao, string resultado)
        => _chamadas.Add(1, new KeyValuePair<string, object?>("operacao", operacao), new KeyValuePair<string, object?>("resultado", resultado));

    public static void Notificacao(string resultado) => _notificacoes.Add(1, new KeyValuePair<string, object?>("resultado", resultado));

    public static void AcaoManual() => _acaoManual.Add(1);
}
