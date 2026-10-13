using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

namespace OficinaMecanica.Billing.API.Adapters.In.Http;

// Consulta do pagamento (CARD-39b). A cobrança nasce do PaymentCreationRequested da Saga; aqui o cliente
// pega o código Pix para pagar e o atendimento acompanha.
[ApiController]
[Route("billing/pagamentos")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class PagamentosController(ConsultarPagamentosUseCase consultar) : ControllerBase
{
    /// <summary>Pagamentos do cliente do token, mais recentes primeiro, com o código Pix.</summary>
    [HttpGet("meus")]
    [Authorize(Roles = Perfis.Cliente)]
    [ProducesResponseType(typeof(IEnumerable<PagamentoResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarMeus(CancellationToken ct)
        => Guid.TryParse(User.FindFirst(OrcamentosController.ClaimClienteId)?.Value, out var clienteId)
            ? Ok(await consultar.ListarDoClienteAsync(clienteId, ct))
            : Forbid();

    /// <summary>Pagamentos de uma OS, para o atendimento informar o cliente.</summary>
    [HttpGet("os/{osId:guid}")]
    [Authorize(Roles = Perfis.AdminEAtendente)]
    [ProducesResponseType(typeof(IEnumerable<PagamentoResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarDaOs(Guid osId, CancellationToken ct) => Ok(await consultar.ListarDaOsAsync(osId, ct));
}

public sealed record DefinirStatusSimuladoRequest
{
    /// <summary>Status no padrão do Mercado Pago: approved, rejected, cancelled, pending ou in_process.</summary>
    [Required]
    public string Status { get; init; } = string.Empty;
}

// Só existe com MercadoPago:Modo=Simulado (ADR-019): faz o papel do cliente pagando o Pix (ou do Pix expirando) e da
// notificação do provedor, para o BDD e o vídeo rodarem sem credenciais. No modo MercadoPago responde 404.
[ApiController]
[Route("billing/simulador/pagamentos")]
[Authorize(Roles = Perfis.Admin)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class SimuladorPagamentosController(IServiceProvider servicos, ReconciliarPagamentoUseCase reconciliar) : ControllerBase
{
    /// <summary>Muda o status do pagamento no provedor simulado e reconcilia, como faria o webhook.</summary>
    /// <param name="referencia">providerReference do pagamento (campo do PaymentPending).</param>
    /// <param name="request">Status novo.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <response code="200">Status aplicado no provedor simulado; o corpo traz o resultado da reconciliação.</response>
    /// <response code="400">Status não suportado pelo simulador.</response>
    /// <response code="404">Fora do modo Simulado, ou referência inexistente no provedor simulado.</response>
    [HttpPost("{referencia}/status")]
    [ProducesResponseType(typeof(ResultadoSimulacaoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DefinirStatus(string referencia, [FromBody] DefinirStatusSimuladoRequest request, CancellationToken ct)
    {
        if (servicos.GetService<ProvedorPagamentoSimulado>() is not { } simulado)
            return NotFound();
        if (!ProvedorPagamentoSimulado.StatusSimulaveis.Contains(request.Status))
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["status"] = [$"Use um destes: {string.Join(", ", ProvedorPagamentoSimulado.StatusSimulaveis)}."]
            }));
        if (!simulado.DefinirStatus(referencia, request.Status))
            return NotFound();

        var resultado = await reconciliar.NotificacaoAsync(new NotificacaoRecebida($"simulador-{Guid.NewGuid()}", referencia, "simulador"), ct);
        return Ok(new ResultadoSimulacaoResponse(referencia, request.Status, resultado));
    }
}

/// <summary>Resultado da simulação.</summary>
/// <param name="Referencia">providerReference do pagamento.</param>
/// <param name="Status">Status aplicado no provedor simulado.</param>
/// <param name="Resultado">O que a reconciliação fez com o pagamento em Billing.</param>
public sealed record ResultadoSimulacaoResponse(string Referencia, string Status, ResultadoNotificacao? Resultado);
