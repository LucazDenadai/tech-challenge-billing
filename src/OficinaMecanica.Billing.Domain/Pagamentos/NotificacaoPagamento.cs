using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Billing.Domain.Comum;

namespace OficinaMecanica.Billing.Domain.Pagamentos;

public enum ResultadoNotificacao
{
    // O pagamento mudou de estado e o evento foi para a outbox.
    Aplicada,
    SemMudanca,
    // O provedor não conhece o recurso, ou ele não é uma tentativa de Billing.
    Desconhecido,
    Divergente,
    ForaDoFluxo,
    // A consulta ao provedor falhou; a reconciliação em segundo plano tenta de novo.
    FalhaTecnica
}

// Auditoria de cada notificação autenticada do webhook (ADR-019). O x-request-id é único:
// a mesma requisição reenviada não é processada de novo. Assinatura inválida não chega aqui; vai para log e métrica.
public class NotificacaoPagamento : EntityBase
{
    public const int TamanhoMaximoRequestId = 200;
    private const int TamanhoMaximoDetalhe = 500;

    public string RequestId { get; private set; } = null!;
    public string ReferenciaRecurso { get; private set; } = null!;
    public string? Acao { get; private set; }
    public DateTimeOffset RecebidaEm { get; private set; }
    public ResultadoNotificacao Resultado { get; private set; }
    public Guid? PagamentoId { get; private set; }
    public string? Detalhe { get; private set; }

    [ExcludeFromCodeCoverage]
    protected NotificacaoPagamento() { }

    public NotificacaoPagamento(string requestId, string referenciaRecurso, string? acao, DateTimeOffset recebidaEm,
        ResultadoNotificacao resultado, Guid? pagamentoId, string? detalhe)
    {
        RequestId = requestId;
        ReferenciaRecurso = referenciaRecurso;
        Acao = acao;
        RecebidaEm = recebidaEm.ToUniversalTime();
        Resultado = resultado;
        PagamentoId = pagamentoId;
        Detalhe = detalhe is { Length: > TamanhoMaximoDetalhe } ? detalhe[..TamanhoMaximoDetalhe] : detalhe;
    }
}
