using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.API.Adapters.In.Http;

// Orçamentos da OS (CARD-39a). Criação só pelo QuoteRequested da Saga; aqui o cliente consulta e decide.
// Só o cliente dono da OS decide: token de funcionário recebe 403, mesmo Admin. Um funcionário que traz
// o próprio veículo entra pelo CPF na Lambda e decide como cliente.
[ApiController]
[Route("billing/orcamentos")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class OrcamentosController(ConsultarOrcamentosUseCase consultar, DecidirOrcamentoUseCase decidir) : ControllerBase
{
    // Claim emitida pela Lambda de autenticação por CPF (RFC-003).
    public const string ClaimClienteId = "cliente_id";

    /// <summary>Orçamentos do cliente do token, mais recentes primeiro, com a versão vigente.</summary>
    [HttpGet("meus")]
    [Authorize(Roles = Perfis.Cliente)]
    [ProducesResponseType(typeof(IEnumerable<OrcamentoResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarMeus(CancellationToken ct)
        => ClienteDoToken() is { } clienteId ? Ok(await consultar.ListarDoClienteAsync(clienteId, ct)) : Forbid();

    /// <summary>Detalhe do orçamento, com versões, itens e histórico de decisão.</summary>
    /// <remarks>O cliente só vê os próprios orçamentos: orçamento de outro cliente responde 404.</remarks>
    [HttpGet("{quoteId:guid}")]
    [Authorize(Roles = Perfis.ClienteEAtendimento)]
    [ProducesResponseType(typeof(OrcamentoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(Guid quoteId, CancellationToken ct)
    {
        if (!User.IsInRole(Perfis.Cliente))
            return Ok(await consultar.ObterAsync(quoteId, null, ct));

        return ClienteDoToken() is { } clienteId ? Ok(await consultar.ObterAsync(quoteId, clienteId, ct)) : Forbid();
    }

    /// <summary>Orçamento de uma OS, para o atendimento informar o cliente.</summary>
    [HttpGet("os/{osId:guid}")]
    [Authorize(Roles = Perfis.AdminEAtendente)]
    [ProducesResponseType(typeof(OrcamentoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ObterPorOs(Guid osId, CancellationToken ct) => Ok(await consultar.ObterPorOsAsync(osId, ct));

    /// <summary>Aprova a versão apresentada. Repetir a mesma aprovação devolve 200 sem novo evento.</summary>
    /// <response code="200">Aprovação registrada (ou já registrada). O evento QuoteApproved sai pela outbox.</response>
    /// <response code="404">Orçamento inexistente, de outro cliente, ou versão inexistente.</response>
    /// <response code="409">Versão desatualizada (há versão mais nova) ou o orçamento já foi recusado.</response>
    /// <response code="410">Orçamento expirado.</response>
    [HttpPost("{quoteId:guid}/aprovar")]
    [Authorize(Roles = Perfis.Cliente)]
    [ProducesResponseType(typeof(OrcamentoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public Task<IActionResult> Aprovar(Guid quoteId, [FromBody] AprovarOrcamentoRequest request, CancellationToken ct)
        => DecidirAsync(quoteId, request.Versao, ResultadoDecisao.Aprovado, null, ct);

    /// <summary>Recusa a versão apresentada, com motivo opcional. Repetir a mesma recusa devolve 200 sem novo evento.</summary>
    /// <response code="200">Recusa registrada (ou já registrada). O evento QuoteDeclined sai pela outbox.</response>
    /// <response code="404">Orçamento inexistente, de outro cliente, ou versão inexistente.</response>
    /// <response code="409">Versão desatualizada (há versão mais nova) ou o orçamento já foi aprovado.</response>
    /// <response code="410">Orçamento expirado.</response>
    [HttpPost("{quoteId:guid}/recusar")]
    [Authorize(Roles = Perfis.Cliente)]
    [ProducesResponseType(typeof(OrcamentoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public Task<IActionResult> Recusar(Guid quoteId, [FromBody] RecusarOrcamentoRequest request, CancellationToken ct)
        => DecidirAsync(quoteId, request.Versao, ResultadoDecisao.Recusado, request.Motivo, ct);

    private async Task<IActionResult> DecidirAsync(Guid quoteId, int versao, ResultadoDecisao decisao, string? motivo, CancellationToken ct)
    {
        if (ClienteDoToken() is not { } clienteId)
            return Forbid();

        var resultado = await decidir.ExecutarAsync(new DecidirOrcamentoCommand(quoteId, versao, decisao, clienteId, motivo), ct);
        var vigente = resultado.Orcamento.VersaoVigente;

        return resultado.Status switch
        {
            StatusTentativaDecisao.Registrada or StatusTentativaDecisao.JaRegistrada => Ok(resultado.Orcamento),
            StatusTentativaDecisao.VersaoInexistente => Problema(StatusCodes.Status404NotFound, "Versão não encontrada",
                $"O orçamento não tem a versão {versao}; a vigente é a {vigente.Versao}.", vigente.Versao),
            StatusTentativaDecisao.VersaoDesatualizada => Problema(StatusCodes.Status409Conflict, "Versão desatualizada",
                $"A versão {versao} foi substituída; decida sobre a versão {vigente.Versao}.", vigente.Versao),
            StatusTentativaDecisao.DecisaoConflitante => Problema(StatusCodes.Status409Conflict, "Orçamento já decidido",
                $"A versão {versao} já está {vigente.Status}.", vigente.Versao),
            StatusTentativaDecisao.Expirada => Problema(StatusCodes.Status410Gone, "Orçamento expirado",
                $"A versão {versao} expirou em {vigente.ExpiraEm:O} sem decisão.", vigente.Versao),
            var outro => throw new InvalidOperationException($"Resultado de decisão inesperado: {outro}.")
        };
    }

    private ObjectResult Problema(int status, string titulo, string detalhe, int versaoVigente)
    {
        var problema = new ProblemDetails { Status = status, Title = titulo, Detail = detalhe };
        problema.Extensions["versaoVigente"] = versaoVigente;
        return new ObjectResult(problema) { StatusCode = status };
    }

    private Guid? ClienteDoToken() => Guid.TryParse(User.FindFirst(ClaimClienteId)?.Value, out var id) ? id : null;
}

public static class Perfis
{
    public const string Cliente = "Cliente";
    public const string Admin = "Admin";
    public const string AdminEAtendente = "Admin,Atendente";
    public const string ClienteEAtendimento = "Cliente,Admin,Atendente";
}
