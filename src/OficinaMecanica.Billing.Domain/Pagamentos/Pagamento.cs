using System.Diagnostics.CodeAnalysis;
using OficinaMecanica.Billing.Domain.Comum;

namespace OficinaMecanica.Billing.Domain.Pagamentos;

// Tentativa de pagamento de uma versão aprovada do orçamento (CARD-39b, ADR-019). O Id é o paymentId dos contratos,
// o external_reference e a chave idempotente enviados ao provedor: todo retry da mesma tentativa usa a mesma chave.
// O estado só muda com o pagamento consultado no provedor; o corpo do webhook nunca é fonte de estado.
public class Pagamento : EntityBase
{
    public Guid OsId { get; private set; }
    public Guid FilialId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public Guid ClienteId { get; private set; }
    public Guid QuoteId { get; private set; }
    public int QuoteVersion { get; private set; }
    public decimal Valor { get; private set; }
    public string Moeda { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    // messageId do PaymentCreationRequested: causationId dos eventos que respondem a ele.
    public Guid MensagemCriacaoId { get; private set; }

    // Só enquanto a cobrança não existe no provedor: a reconciliação pode precisar refazer a criação (ADR-019).
    public string? EmailPagador { get; private set; }
    public DateTimeOffset ExpiraEm { get; private set; }

    public StatusPagamento Status { get; private set; }
    public string? ReferenciaProvedor { get; private set; }
    public string? StatusProvedor { get; private set; }
    public string? CodigoPix { get; private set; }
    public string? UrlPagamento { get; private set; }
    public DateTimeOffset? AprovadoEm { get; private set; }
    public string? MotivoRecusa { get; private set; }
    public bool ResultadoDesconhecidoPublicado { get; private set; }

    public string? EstornoIdempotencyKey { get; private set; }
    public Guid? MensagemEstornoId { get; private set; }
    public string? MotivoEstorno { get; private set; }
    public string? ReferenciaEstorno { get; private set; }
    public bool EstornoPendentePublicado { get; private set; }
    public DateTimeOffset? EstornadoEm { get; private set; }
    public string? MotivoFalhaEstorno { get; private set; }

    // Falhas técnicas seguidas (timeout, 5xx, rede). Zera quando o provedor responde.
    public int FalhasTecnicas { get; private set; }
    public bool ReconciliacaoManual { get; private set; }
    public string? MotivoReconciliacaoManual { get; private set; }
    // Quando a reconciliação em segundo plano deve olhar de novo. Nulo: nada a verificar.
    public DateTimeOffset? ProximaVerificacaoEm { get; private set; }

    [ExcludeFromCodeCoverage]
    protected Pagamento() { }

    public static Pagamento Criar(Guid osId, Guid filialId, Guid correlationId, Guid clienteId, Guid quoteId, int quoteVersion,
        decimal valor, string moeda, string idempotencyKey, Guid mensagemCriacaoId, string? emailPagador,
        DateTimeOffset agora, TimeSpan expiracao, TimeSpan intervaloVerificacao)
    {
        if (osId == Guid.Empty || filialId == Guid.Empty || clienteId == Guid.Empty || quoteId == Guid.Empty)
            throw new PagamentoInvalidoException("OS, filial, cliente e orçamento são obrigatórios no pagamento.");
        if (valor <= 0 || decimal.Round(valor, 2) != valor)
            throw new PagamentoInvalidoException("O valor do pagamento deve ser positivo e ter no máximo duas casas decimais.");
        if (moeda != Dinheiro.Moeda)
            throw new PagamentoInvalidoException($"O Mercado Pago no Brasil cobra só em {Dinheiro.Moeda}.");
        if (string.IsNullOrWhiteSpace(emailPagador))
            throw new PagamentoInvalidoException("O Pix exige o e-mail do pagador (payerEmail).");

        return new Pagamento
        {
            OsId = osId,
            FilialId = filialId,
            CorrelationId = correlationId,
            ClienteId = clienteId,
            QuoteId = quoteId,
            QuoteVersion = quoteVersion,
            Valor = valor,
            Moeda = moeda,
            IdempotencyKey = idempotencyKey,
            MensagemCriacaoId = mensagemCriacaoId,
            EmailPagador = emailPagador.Trim(),
            ExpiraEm = (agora + expiracao).ToUniversalTime(),
            Status = StatusPagamento.Solicitado,
            ProximaVerificacaoEm = (agora + intervaloVerificacao).ToUniversalTime()
        };
    }

    public bool PrecisaCriarNoProvedor => Status == StatusPagamento.Solicitado;

    public bool PrecisaPedirEstorno => Status == StatusPagamento.EstornoPendente && ReferenciaEstorno is null;

    // Aplica o pagamento consultado no provedor. Só avança; a mesma informação de novo não gera evento.
    public Reconciliacao Reconciliar(PagamentoNoProvedor noProvedor, DateTimeOffset agora, TimeSpan intervaloVerificacao)
    {
        if (Divergencia(noProvedor) is { } divergencia)
            return new Reconciliacao(ResultadoReconciliacao.Divergente, null, divergencia);

        FalhasTecnicas = 0;
        StatusProvedor = noProvedor.StatusOriginal;
        ReferenciaProvedor ??= noProvedor.Referencia;
        CodigoPix ??= noProvedor.CodigoPix;
        UrlPagamento ??= noProvedor.UrlPagamento;

        var aguardandoPagamento = Status is StatusPagamento.Solicitado or StatusPagamento.Pendente;
        var reconciliacao = noProvedor.Status switch
        {
            StatusNoProvedor.Pendente when Status == StatusPagamento.Solicitado
                => Mudar(StatusPagamento.Pendente, EventoPagamento.Pendente),
            StatusNoProvedor.Aprovado when aguardandoPagamento
                => Aprovar(noProvedor.AprovadoEm ?? agora),
            StatusNoProvedor.Recusado when aguardandoPagamento
                => Recusar(noProvedor.StatusOriginal),
            StatusNoProvedor.Estornado when Status == StatusPagamento.EstornoPendente
                => Estornar(agora),
            // Estorno sem pedido da Saga (painel do provedor) ou depois de desistir: a Saga não prevê.
            StatusNoProvedor.Estornado when Status != StatusPagamento.Estornado
                => new Reconciliacao(ResultadoReconciliacao.ForaDoFluxo, null, $"Pagamento {noProvedor.StatusOriginal} no provedor com estado {Status} em Billing."),
            StatusNoProvedor.ForaDoFluxo
                => new Reconciliacao(ResultadoReconciliacao.ForaDoFluxo, null, $"Status {noProvedor.StatusOriginal} no provedor."),
            StatusNoProvedor.Recusado when Status == StatusPagamento.Aprovado
                => new Reconciliacao(ResultadoReconciliacao.ForaDoFluxo, null, $"Pagamento aprovado em Billing e {noProvedor.StatusOriginal} no provedor."),
            _ => new Reconciliacao(ResultadoReconciliacao.SemMudanca, null)
        };

        if (reconciliacao.Resultado == ResultadoReconciliacao.ForaDoFluxo)
            MarcarParaAcaoManual(reconciliacao.Detalhe!);

        if (Status != StatusPagamento.Solicitado)
            EmailPagador = null;

        ProximaVerificacaoEm = Status is StatusPagamento.Pendente or StatusPagamento.EstornoPendente && !ReconciliacaoManual
            ? (agora + intervaloVerificacao).ToUniversalTime()
            : null;

        MarcarAtualizado();
        return reconciliacao;
    }

    // Timeout, 5xx, 429 ou rede: o resultado continua desconhecido (nunca é recusa). Depois do limite, ação manual.
    public EventoPagamento? RegistrarFalhaTecnica(DateTimeOffset agora, TimeSpan intervaloVerificacao, int maxTentativas)
    {
        FalhasTecnicas++;
        ProximaVerificacaoEm = (agora + intervaloVerificacao).ToUniversalTime();
        MarcarAtualizado();

        if (Status == StatusPagamento.EstornoPendente)
        {
            if (FalhasTecnicas >= maxTentativas)
            {
                MotivoFalhaEstorno = $"Estorno sem confirmação do provedor após {FalhasTecnicas} tentativas.";
                ProximaVerificacaoEm = null;
                Status = StatusPagamento.EstornoFalhou;
                return EventoPagamento.EstornoFalhou;
            }

            if (EstornoPendentePublicado)
                return null;
            EstornoPendentePublicado = true;
            return EventoPagamento.EstornoPendente;
        }

        if (FalhasTecnicas >= maxTentativas)
            MarcarParaAcaoManual($"Sem resposta do provedor após {FalhasTecnicas} tentativas.");

        if (Status != StatusPagamento.Solicitado || ResultadoDesconhecidoPublicado)
            return null;
        ResultadoDesconhecidoPublicado = true;
        return EventoPagamento.ResultadoDesconhecido;
    }

    public void MarcarParaAcaoManual(string motivo)
    {
        ReconciliacaoManual = true;
        MotivoReconciliacaoManual = motivo;
        ProximaVerificacaoEm = null;
        MarcarAtualizado();
    }

    // Compensação da Saga (ADR-017): estorno total. Pedido repetido devolve o que já está gravado.
    public ResultadoPedidoEstorno SolicitarEstorno(string idempotencyKey, Guid mensagemId, decimal valor, string moeda, string motivo,
        DateTimeOffset agora, TimeSpan intervaloVerificacao)
    {
        if (Status is StatusPagamento.EstornoPendente or StatusPagamento.Estornado or StatusPagamento.EstornoFalhou)
            return ResultadoPedidoEstorno.JaSolicitado;
        if (Status != StatusPagamento.Aprovado)
            throw new PagamentoInvalidoException($"Pagamento no estado {Status} não pode ser estornado.");
        if (valor != Valor || moeda != Moeda)
            throw new PagamentoInvalidoException($"O estorno deve ser total: {Valor} {Moeda}, não {valor} {moeda}.");

        Status = StatusPagamento.EstornoPendente;
        EstornoIdempotencyKey = idempotencyKey;
        MensagemEstornoId = mensagemId;
        MotivoEstorno = motivo;
        FalhasTecnicas = 0;
        ReconciliacaoManual = false;
        MotivoReconciliacaoManual = null;
        ProximaVerificacaoEm = (agora + intervaloVerificacao).ToUniversalTime();
        MarcarAtualizado();
        return ResultadoPedidoEstorno.Aceito;
    }

    // Evento que representa o estorno já gravado, para responder a um pedido repetido.
    public EventoPagamento EventoDoEstornoAtual => Status switch
    {
        StatusPagamento.EstornoPendente => EventoPagamento.EstornoPendente,
        StatusPagamento.Estornado => EventoPagamento.Estornado,
        StatusPagamento.EstornoFalhou => EventoPagamento.EstornoFalhou,
        _ => throw new InvalidOperationException($"Pagamento no estado {Status} não tem estorno.")
    };

    public EventoPagamento? RegistrarEstorno(EstornoNoProvedor estorno, DateTimeOffset agora, TimeSpan intervaloVerificacao)
    {
        if (Status != StatusPagamento.EstornoPendente)
            return null;

        ReferenciaEstorno ??= estorno.Referencia;
        FalhasTecnicas = 0;
        MarcarAtualizado();

        switch (estorno.Status)
        {
            case StatusEstornoNoProvedor.Aprovado:
                return Estornar(agora).Evento;

            case StatusEstornoNoProvedor.Recusado:
                MotivoFalhaEstorno = $"Estorno {estorno.StatusOriginal} no provedor.";
                ProximaVerificacaoEm = null;
                Status = StatusPagamento.EstornoFalhou;
                return EventoPagamento.EstornoFalhou;

            default:
                ProximaVerificacaoEm = (agora + intervaloVerificacao).ToUniversalTime();
                if (EstornoPendentePublicado)
                    return null;
                EstornoPendentePublicado = true;
                return EventoPagamento.EstornoPendente;
        }
    }

    private string? Divergencia(PagamentoNoProvedor noProvedor)
    {
        if (!Guid.TryParse(noProvedor.ReferenciaExterna, out var referenciaExterna) || referenciaExterna != Id)
            return "external_reference do provedor não é este pagamento.";
        if (ReferenciaProvedor is not null && ReferenciaProvedor != noProvedor.Referencia)
            return "O provedor devolveu outro pagamento para esta tentativa.";
        if (noProvedor.Valor != Valor || noProvedor.Moeda != Moeda)
            return "Valor ou moeda no provedor diferem da tentativa.";
        return null;
    }

    private Reconciliacao Mudar(StatusPagamento novo, EventoPagamento evento)
    {
        // A transição resolve o que estava parado esperando ação manual.
        ReconciliacaoManual = false;
        MotivoReconciliacaoManual = null;
        Status = novo;
        return new Reconciliacao(ResultadoReconciliacao.Transicao, evento);
    }

    private Reconciliacao Aprovar(DateTimeOffset aprovadoEm)
    {
        AprovadoEm = aprovadoEm.ToUniversalTime();
        return Mudar(StatusPagamento.Aprovado, EventoPagamento.Aprovado);
    }

    private Reconciliacao Recusar(string statusOriginal)
    {
        MotivoRecusa = $"Pagamento {statusOriginal} no Mercado Pago.";
        return Mudar(StatusPagamento.Recusado, EventoPagamento.Recusado);
    }

    private Reconciliacao Estornar(DateTimeOffset agora)
    {
        EstornadoEm = agora.ToUniversalTime();
        ProximaVerificacaoEm = null;
        return Mudar(StatusPagamento.Estornado, EventoPagamento.Estornado);
    }
}
