namespace OficinaMecanica.Billing.Domain.Pagamentos;

// Estados do pagamento (ADR-019). Só avançam: notificação atrasada ou fora de ordem não regride estado.
public enum StatusPagamento
{
    // Registrado em Billing; a cobrança ainda não foi confirmada no provedor.
    Solicitado,
    // Cobrança criada no provedor, aguardando o cliente pagar.
    Pendente,
    Aprovado,
    // Recusado ou cancelado no provedor (inclui Pix expirado). Terminal.
    Recusado,
    // Estorno pedido pela Saga e ainda não confirmado.
    EstornoPendente,
    Estornado,
    // Estorno recusado pelo provedor ou sem confirmação após o limite de tentativas. Terminal; ação manual.
    EstornoFalhou
}

// Status do pagamento no provedor, já traduzido pelo adapter.
public enum StatusNoProvedor
{
    Pendente,
    Aprovado,
    Recusado,
    Estornado,
    // charged_back, in_mediation ou status desconhecido: não muda estado, exige ação manual.
    ForaDoFluxo
}

public enum StatusEstornoNoProvedor
{
    Aprovado,
    Pendente,
    Recusado
}

// Pagamento como o provedor o devolve. ReferenciaExterna é o paymentId que Billing enviou na criação.
public sealed record PagamentoNoProvedor(
    string Referencia,
    string? ReferenciaExterna,
    decimal Valor,
    string Moeda,
    StatusNoProvedor Status,
    string StatusOriginal,
    DateTimeOffset? AprovadoEm,
    string? CodigoPix,
    string? UrlPagamento);

public sealed record EstornoNoProvedor(string Referencia, StatusEstornoNoProvedor Status, string StatusOriginal);

// O evento da Saga que uma mudança de estado exige. Cada um sai uma vez, na transição.
public enum EventoPagamento
{
    Pendente,
    Aprovado,
    Recusado,
    ResultadoDesconhecido,
    EstornoPendente,
    Estornado,
    EstornoFalhou
}

public enum ResultadoReconciliacao
{
    // O estado mudou; há evento a publicar.
    Transicao,
    // Nada a mudar: estado igual ou notificação mais antiga que o estado gravado.
    SemMudanca,
    // O pagamento consultado não corresponde à tentativa (referência, valor ou moeda). Não muda nada.
    Divergente,
    // Status que a Saga não prevê (estorno fora da Saga, contestação). Não muda nada; ação manual.
    ForaDoFluxo
}

public sealed record Reconciliacao(ResultadoReconciliacao Resultado, EventoPagamento? Evento, string? Detalhe = null);

public enum ResultadoPedidoEstorno
{
    // Aprovado → EstornoPendente: falta chamar o provedor.
    Aceito,
    // Já havia estorno: devolve o resultado gravado, sem nova chamada.
    JaSolicitado
}

// Comando que não pode gerar cobrança ou estorno (orçamento não aprovado, valor diferente...). Vai para a DLQ.
public class PagamentoInvalidoException(string motivo) : Exception(motivo);
