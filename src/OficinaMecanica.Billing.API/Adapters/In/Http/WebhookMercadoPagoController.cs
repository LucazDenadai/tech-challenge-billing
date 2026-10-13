using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Webhook;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

namespace OficinaMecanica.Billing.API.Adapters.In.Http;

// Webhook do Mercado Pago (ADR-019). Público, mas só aceita notificação com x-signature válido. O corpo nunca é
// fonte de estado: a notificação só diz qual pagamento consultar no provedor. Responder 200 significa "recebido",
// não "pago". Falha na consulta também responde 200: a reconciliação em segundo plano tenta de novo.
[ApiController]
[Route("billing/pagamentos/webhook/mercadopago")]
[AllowAnonymous]
public class WebhookMercadoPagoController(
    ReconciliarPagamentoUseCase reconciliar,
    IOptions<MercadoPagoOptions> options,
    TimeProvider relogio,
    ILogger<WebhookMercadoPagoController> logger) : ControllerBase
{
    /// <summary>Recebe a notificação de pagamento do Mercado Pago.</summary>
    /// <remarks>
    /// Exige os cabeçalhos <c>x-signature</c> (<c>ts=...,v1=...</c>) e <c>x-request-id</c> e o parâmetro <c>data.id</c>.
    /// O mesmo <c>x-request-id</c> reenviado não é processado de novo. Notificações de outros tipos são ignoradas.
    /// </remarks>
    /// <response code="200">Notificação recebida (processada, repetida ou ignorada).</response>
    /// <response code="400">Sem data.id ou sem x-request-id.</response>
    /// <response code="401">Assinatura ausente, inválida ou fora da tolerância de tempo.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Receber(
        [FromQuery(Name = "data.id")] string? dataId,
        [FromQuery(Name = "type")] string? tipo,
        [FromHeader(Name = "x-signature")] string? assinatura,
        [FromHeader(Name = "x-request-id")] string? requestId,
        CancellationToken ct)
    {
        var (tipoNoCorpo, acao, dataIdNoCorpo) = await LerCorpoAsync(ct);
        dataId ??= dataIdNoCorpo;
        tipo ??= tipoNoCorpo;

        if (string.IsNullOrWhiteSpace(dataId) || dataId.Length > 100
            || string.IsNullOrWhiteSpace(requestId) || requestId.Length > NotificacaoPagamento.TamanhoMaximoRequestId)
            return BadRequest();

        var opcoes = options.Value;
        var validador = new ValidadorAssinaturaMercadoPago(opcoes.WebhookSecret, TimeSpan.FromMinutes(opcoes.ToleranciaAssinaturaMinutos), relogio);
        var resultado = validador.Validar(assinatura, requestId, dataId);
        if (resultado != ResultadoAssinatura.Valida)
        {
            MetricasPagamento.Notificacao($"Assinatura{resultado}");
            logger.LogWarning("Webhook do Mercado Pago recusado: assinatura {Resultado}. RequestId={RequestId}", resultado, requestId);
            return Unauthorized();
        }

        if (tipo is not "payment")
        {
            MetricasPagamento.Notificacao("Ignorada");
            return Ok();
        }

        await reconciliar.NotificacaoAsync(new NotificacaoRecebida(requestId, dataId, acao), ct);
        return Ok();
    }

    // O corpo é opcional e só complementa a query; corpo ilegível não impede a validação.
    private async Task<(string? Tipo, string? Acao, string? DataId)> LerCorpoAsync(CancellationToken ct)
    {
        try
        {
            using var corpo = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            var raiz = corpo.RootElement;
            return (Texto(raiz, "type"), Texto(raiz, "action"),
                raiz.TryGetProperty("data", out var dados) ? Texto(dados, "id") : null);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Texto(JsonElement elemento, string propriedade)
            => elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(propriedade, out var valor)
                ? valor.ValueKind switch
                {
                    JsonValueKind.String => valor.GetString(),
                    JsonValueKind.Number => valor.GetRawText(),
                    _ => null
                }
                : null;
    }
}
