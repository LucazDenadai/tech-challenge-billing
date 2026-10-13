using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.Application.Ports.In;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Application.UseCases.Pagamentos;
using OficinaMecanica.Billing.Domain.Pagamentos;

namespace OficinaMecanica.Billing.UnitTests.UseCases;

// Pagamentos e notificações em memória. A gravação real (outbox, concorrência) é testada contra o PostgreSQL.
public sealed class PagamentosEmMemoria : IPagamentoRepository, INotificacaoPagamentoRepository
{
    private readonly List<Pagamento> _pagamentos = [];

    public List<NotificacaoPagamento> Notificacoes { get; } = [];
    public IReadOnlyList<Pagamento> Pagamentos => _pagamentos;

    public Task<Pagamento?> ObterPorIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_pagamentos.FirstOrDefault(p => p.Id == id));

    public Task AdicionarAsync(Pagamento entidade, CancellationToken ct = default)
    {
        _pagamentos.Add(entidade);
        return Task.CompletedTask;
    }

    public Task<Pagamento?> ObterPorIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => Task.FromResult(_pagamentos.FirstOrDefault(p => p.IdempotencyKey == idempotencyKey));

    public Task<Pagamento?> ObterPorOrcamentoAsync(Guid quoteId, int quoteVersion, CancellationToken ct = default)
        => Task.FromResult(_pagamentos.FirstOrDefault(p => p.QuoteId == quoteId && p.QuoteVersion == quoteVersion));

    public Task<IReadOnlyList<Pagamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Pagamento>>(_pagamentos.Where(p => p.ClienteId == clienteId).ToList());

    public Task<IReadOnlyList<Pagamento>> ListarPorOsAsync(Guid osId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Pagamento>>(_pagamentos.Where(p => p.OsId == osId).ToList());

    public Task<IReadOnlyList<Guid>> ListarParaVerificarAsync(DateTimeOffset agora, int limite, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(_pagamentos
            .Where(p => p.ProximaVerificacaoEm <= agora && !p.ReconciliacaoManual)
            .OrderBy(p => p.ProximaVerificacaoEm)
            .Take(limite)
            .Select(p => p.Id)
            .ToList());

    public Task<bool> ExisteAsync(string requestId, CancellationToken ct = default)
        => Task.FromResult(Notificacoes.Any(n => n.RequestId == requestId));

    Task INotificacaoPagamentoRepository.AdicionarAsync(NotificacaoPagamento notificacao, CancellationToken ct)
    {
        Notificacoes.Add(notificacao);
        return Task.CompletedTask;
    }
}

// Provedor programável: cada operação devolve a próxima resposta da fila; sem resposta na fila, simula o
// comportamento normal do Mercado Pago (cria Pendente, guarda pela chave idempotente, estorna aprovado).
public sealed class ProvedorFalso : IProvedorPagamento
{
    private long _proximo = 1000;
    private readonly Dictionary<string, PagamentoNoProvedor> _porChave = [];

    public Dictionary<string, PagamentoNoProvedor> Pagamentos { get; } = [];
    public Queue<string> FalhasCriacao { get; } = new();
    public Queue<string> FalhasConsulta { get; } = new();
    public Queue<ResultadoProvedor<EstornoNoProvedor>> RespostasEstorno { get; } = new();
    public List<NovaCobranca> Criacoes { get; } = [];
    public List<string> ChavesEstorno { get; } = [];
    public bool LancarNaCriacao { get; set; }

    public Task<ResultadoProvedor<PagamentoNoProvedor>> CriarAsync(NovaCobranca cobranca, CancellationToken ct = default)
    {
        Criacoes.Add(cobranca);
        if (LancarNaCriacao)
            throw new HttpRequestException("Conexão recusada.");
        if (FalhasCriacao.TryDequeue(out var falha))
            return Task.FromResult(ResultadoProvedor<PagamentoNoProvedor>.FalhaTecnica(falha));

        if (!_porChave.TryGetValue(cobranca.ChaveIdempotencia, out var pagamento))
        {
            pagamento = new PagamentoNoProvedor((_proximo++).ToString(), cobranca.PagamentoId.ToString(), cobranca.Valor, cobranca.Moeda,
                StatusNoProvedor.Pendente, "pending", null, "00020126pix-falso", "https://provedor.falso/pix");
            _porChave[cobranca.ChaveIdempotencia] = pagamento;
            Pagamentos[pagamento.Referencia] = pagamento;
        }

        return Task.FromResult(ResultadoProvedor<PagamentoNoProvedor>.Sucesso(Pagamentos[pagamento.Referencia]));
    }

    public Task<ResultadoProvedor<PagamentoNoProvedor>> ObterAsync(string referencia, CancellationToken ct = default)
        => Task.FromResult(FalhasConsulta.TryDequeue(out var falha)
            ? ResultadoProvedor<PagamentoNoProvedor>.FalhaTecnica(falha)
            : ResultadoProvedor<PagamentoNoProvedor>.Sucesso(Pagamentos.GetValueOrDefault(referencia)));

    public Task<ResultadoProvedor<PagamentoNoProvedor>> BuscarPorPagamentoAsync(Guid pagamentoId, CancellationToken ct = default)
        => Task.FromResult(ResultadoProvedor<PagamentoNoProvedor>.Sucesso(
            Pagamentos.Values.FirstOrDefault(p => p.ReferenciaExterna == pagamentoId.ToString())));

    public Task<ResultadoProvedor<EstornoNoProvedor>> EstornarAsync(string referencia, decimal valor, string chaveIdempotencia, CancellationToken ct = default)
    {
        ChavesEstorno.Add(chaveIdempotencia);
        if (RespostasEstorno.TryDequeue(out var resposta))
            return Task.FromResult(resposta);

        Mudar(referencia, StatusNoProvedor.Estornado, "refunded");
        return Task.FromResult(ResultadoProvedor<EstornoNoProvedor>.Sucesso(new EstornoNoProvedor($"r-{referencia}", StatusEstornoNoProvedor.Aprovado, "approved")));
    }

    // Simula o cliente pagando, o Pix expirando ou um estorno concluído.
    public void Mudar(string referencia, StatusNoProvedor status, string original, decimal? valor = null)
        => Pagamentos[referencia] = Pagamentos[referencia] with
        {
            Status = status,
            StatusOriginal = original,
            Valor = valor ?? Pagamentos[referencia].Valor,
            AprovadoEm = status == StatusNoProvedor.Aprovado ? DateTimeOffset.UtcNow : Pagamentos[referencia].AprovadoEm
        };

    // Pagamento criado no provedor sem que a resposta tenha chegado a Billing.
    public void CriarSemResponder(Guid pagamentoId, decimal valor)
    {
        var pagamento = new PagamentoNoProvedor((_proximo++).ToString(), pagamentoId.ToString(), valor, "BRL",
            StatusNoProvedor.Pendente, "pending", null, "pix", null);
        Pagamentos[pagamento.Referencia] = pagamento;
    }
}

// Orçamento aprovado fixo, no lugar do módulo de orçamento.
public sealed class OrcamentosAprovados : IOrcamentoParaPagamento
{
    public List<OrcamentoAprovado> Aprovados { get; } = [];

    public Task<OrcamentoAprovado?> ObterVersaoAprovadaAsync(Guid quoteId, int versao, CancellationToken ct = default)
        => Task.FromResult(Aprovados.FirstOrDefault(a => a.QuoteId == quoteId && a.Versao == versao));
}

// Monta o processador da Saga e os casos de uso de pagamento sobre os armazenamentos em memória.
public sealed class MontagemPagamentos
{
    public static readonly ConfiguracaoPagamento Configuracao = new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), MaxTentativas: 3);

    public MontagemPagamentos(ArmazenamentoEmMemoria store, TimeProvider relogio, SolicitarOrcamentoUseCase? solicitarOrcamento = null)
    {
        Store = store;
        Cobrar = new CobrarPagamentoUseCase(Pagamentos, Provedor, store, store, Configuracao, relogio, NullLogger<CobrarPagamentoUseCase>.Instance);
        Estornar = new EstornarPagamentoUseCase(Pagamentos, Provedor, store, store, Configuracao, relogio, NullLogger<EstornarPagamentoUseCase>.Instance);
        Reconciliar = new ReconciliarPagamentoUseCase(Pagamentos, Pagamentos, Provedor, store, store, Configuracao, relogio,
            NullLogger<ReconciliarPagamentoUseCase>.Instance);
        Verificar = new VerificarPagamentosUseCase(Pagamentos, Cobrar, Estornar, Reconciliar, store, relogio);
        Processar = new ProcessarMensagemSagaUseCase(store, store, store, store,
            solicitarOrcamento ?? new SolicitarOrcamentoUseCase(store, new ConfiguracaoOrcamento(TimeSpan.FromHours(24)), relogio),
            new SolicitarPagamentoUseCase(Pagamentos, Orcamentos, Configuracao, relogio),
            new SolicitarEstornoUseCase(Pagamentos, Configuracao, relogio),
            Cobrar, Estornar, NullLogger<ProcessarMensagemSagaUseCase>.Instance);
    }

    public ArmazenamentoEmMemoria Store { get; }
    public PagamentosEmMemoria Pagamentos { get; } = new();
    public ProvedorFalso Provedor { get; } = new();
    public OrcamentosAprovados Orcamentos { get; } = new();
    public CobrarPagamentoUseCase Cobrar { get; }
    public EstornarPagamentoUseCase Estornar { get; }
    public ReconciliarPagamentoUseCase Reconciliar { get; }
    public VerificarPagamentosUseCase Verificar { get; }
    public ProcessarMensagemSagaUseCase Processar { get; }
}
