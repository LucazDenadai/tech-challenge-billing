using System.Collections.Concurrent;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

// Provedor em memória para MercadoPago:Modo=Simulado (ADR-019). Imita o que Billing usa do Mercado Pago:
// criação idempotente pela chave, Pix pendente, consulta, busca pelo external_reference e estorno idempotente.
// O status muda só pela rota do simulador (cliente pagou, recusou ou o Pix expirou). Os dados somem ao reiniciar.
public class ProvedorPagamentoSimulado(TimeProvider relogio) : IProvedorPagamento
{
    // A numeração começa no instante da subida, em milissegundos: os pagamentos ficam no banco do Billing e o
    // simulador reinicia do zero. Com um valor fixo, o primeiro pagamento depois de um restart repetia a referência
    // de um pagamento antigo e violava IX_Pagamentos_ReferenciaProvedor (achado na demonstração do CARD-42b).
    private long _proximaReferencia = relogio.GetUtcNow().ToUnixTimeMilliseconds();
    private readonly ConcurrentDictionary<string, PagamentoNoProvedor> _pagamentos = new();
    private readonly ConcurrentDictionary<string, string> _referenciaPorChave = new();
    private readonly ConcurrentDictionary<string, EstornoNoProvedor> _estornoPorChave = new();

    public Task<ResultadoProvedor<PagamentoNoProvedor>> CriarAsync(NovaCobranca cobranca, CancellationToken ct = default)
    {
        var referencia = _referenciaPorChave.GetOrAdd(cobranca.ChaveIdempotencia, _ =>
        {
            var nova = Interlocked.Increment(ref _proximaReferencia).ToString();
            _pagamentos[nova] = new PagamentoNoProvedor(nova, cobranca.PagamentoId.ToString(), cobranca.Valor, cobranca.Moeda,
                StatusNoProvedor.Pendente, "pending", null,
                $"00020126580014br.gov.bcb.pix-SIMULADO-{nova}", $"https://simulador.billing.local/pix/{nova}");
            return nova;
        });
        return Task.FromResult(ResultadoProvedor<PagamentoNoProvedor>.Sucesso(_pagamentos[referencia]));
    }

    public Task<ResultadoProvedor<PagamentoNoProvedor>> ObterAsync(string referencia, CancellationToken ct = default)
        => Task.FromResult(ResultadoProvedor<PagamentoNoProvedor>.Sucesso(_pagamentos.GetValueOrDefault(referencia)));

    public Task<ResultadoProvedor<PagamentoNoProvedor>> BuscarPorPagamentoAsync(Guid pagamentoId, CancellationToken ct = default)
        => Task.FromResult(ResultadoProvedor<PagamentoNoProvedor>.Sucesso(
            _pagamentos.Values.FirstOrDefault(p => p.ReferenciaExterna == pagamentoId.ToString())));

    public Task<ResultadoProvedor<EstornoNoProvedor>> EstornarAsync(string referencia, decimal valor, string chaveIdempotencia, CancellationToken ct = default)
    {
        if (!_pagamentos.TryGetValue(referencia, out var pagamento) || pagamento.Status is not (StatusNoProvedor.Aprovado or StatusNoProvedor.Estornado))
            return Task.FromResult(ResultadoProvedor<EstornoNoProvedor>.Sucesso(new EstornoNoProvedor($"r-{referencia}", StatusEstornoNoProvedor.Recusado, "rejected")));

        var estorno = _estornoPorChave.GetOrAdd(chaveIdempotencia, _ =>
        {
            _pagamentos[referencia] = pagamento with { Status = StatusNoProvedor.Estornado, StatusOriginal = "refunded" };
            return new EstornoNoProvedor($"r-{referencia}", StatusEstornoNoProvedor.Aprovado, "approved");
        });
        return Task.FromResult(ResultadoProvedor<EstornoNoProvedor>.Sucesso(estorno));
    }

    // Status aceitos pela rota do simulador, com o mesmo nome usado pelo Mercado Pago.
    public static readonly IReadOnlyList<string> StatusSimulaveis = ["approved", "rejected", "cancelled", "pending", "in_process"];

    // Muda o status no "provedor". Devolve false se a referência não existe.
    public bool DefinirStatus(string referencia, string status)
    {
        if (!_pagamentos.TryGetValue(referencia, out var pagamento))
            return false;

        _pagamentos[referencia] = pagamento with
        {
            Status = ProvedorMercadoPago.MapearStatus(status),
            StatusOriginal = status,
            AprovadoEm = status == "approved" ? DateTimeOffset.UtcNow : pagamento.AprovadoEm
        };
        return true;
    }
}
