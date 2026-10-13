using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Application.UseCases.Pagamentos;

/// <summary>Pagamento de uma OS, com o que o cliente precisa para pagar o Pix.</summary>
/// <param name="PaymentId">Identificador do pagamento nos contratos da Saga.</param>
/// <param name="OsId">OS cobrada.</param>
/// <param name="QuoteId">Orçamento cobrado.</param>
/// <param name="QuoteVersion">Versão aprovada do orçamento.</param>
/// <param name="Valor">Valor cobrado, igual ao da versão aprovada.</param>
/// <param name="Moeda">Sempre BRL.</param>
/// <param name="Status">Estado em Billing. Só Aprovado libera a execução.</param>
/// <param name="CodigoPix">Código Pix "copia e cola". Válido até ExpiraEm.</param>
/// <param name="UrlPagamento">Página de pagamento do provedor, com o QR Code.</param>
/// <param name="ExpiraEm">Fim da validade do Pix. Depois disso o pagamento é recusado.</param>
/// <param name="AprovadoEm">Quando o provedor aprovou.</param>
/// <param name="MotivoRecusa">Status do provedor que recusou ou cancelou o pagamento.</param>
/// <param name="EstornadoEm">Quando o estorno foi confirmado.</param>
/// <param name="MotivoFalhaEstorno">Por que o estorno falhou.</param>
/// <param name="ReconciliacaoManual">O pagamento parou esperando ação manual (sem resposta do provedor ou estado fora do fluxo).</param>
// ReferenciaProvedor é o ID da cobrança no Mercado Pago: permite ao atendimento conferir o pagamento no painel
// e, no modo Simulado, usar a rota do simulador (CARD-40c). Não é dado do pagador.
public sealed record PagamentoResponse(
    Guid PaymentId, Guid OsId, Guid QuoteId, int QuoteVersion, decimal Valor, string Moeda, StatusPagamento Status,
    string? CodigoPix, string? UrlPagamento, DateTimeOffset ExpiraEm, DateTimeOffset? AprovadoEm, string? MotivoRecusa,
    DateTimeOffset? EstornadoEm, string? MotivoFalhaEstorno, bool ReconciliacaoManual, string? ReferenciaProvedor)
{
    public static PagamentoResponse De(Pagamento p) => new(
        p.Id, p.OsId, p.QuoteId, p.QuoteVersion, p.Valor, p.Moeda, p.Status, p.CodigoPix, p.UrlPagamento, p.ExpiraEm, p.AprovadoEm,
        p.MotivoRecusa, p.EstornadoEm, p.MotivoFalhaEstorno, p.ReconciliacaoManual, p.ReferenciaProvedor);
}

public class ConsultarPagamentosUseCase(IPagamentoRepository repository)
{
    public async Task<IReadOnlyList<PagamentoResponse>> ListarDoClienteAsync(Guid clienteId, CancellationToken ct = default)
        => (await repository.ListarPorClienteAsync(clienteId, ct)).Select(PagamentoResponse.De).ToList();

    public async Task<IReadOnlyList<PagamentoResponse>> ListarDaOsAsync(Guid osId, CancellationToken ct = default)
        => (await repository.ListarPorOsAsync(osId, ct)).Select(PagamentoResponse.De).ToList();
}
