using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.UnitTests.UseCases;

// Repositório, inbox, outbox e unidade de trabalho em memória. A outbox só recebe os eventos quando
// SalvarAsync é chamado, como na transação real. Concorrência real é testada contra o PostgreSQL.
public sealed class ArmazenamentoEmMemoria : IOrcamentoRepository, IInboxRepository, IOutbox, IUnidadeDeTrabalho, ITransacao
{
    private readonly List<Orcamento> _orcamentos = [];
    private readonly HashSet<Guid> _inbox = [];
    private readonly List<MensagemSaga> _pendentes = [];

    public List<MensagemSaga> Publicadas { get; } = [];
    public int Gravacoes { get; private set; }

    public IReadOnlyList<Orcamento> Orcamentos => _orcamentos;

    public Task<Orcamento?> ObterPorIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_orcamentos.FirstOrDefault(o => o.Id == id));

    public Task AdicionarAsync(Orcamento entidade, CancellationToken ct = default)
    {
        _orcamentos.Add(entidade);
        return Task.CompletedTask;
    }

    public Task<Orcamento?> ObterPorOsAsync(Guid osId, CancellationToken ct = default)
        => Task.FromResult(_orcamentos.FirstOrDefault(o => o.OsId == osId));

    public Task<IReadOnlyList<Orcamento>> ListarPorClienteAsync(Guid clienteId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Orcamento>>(_orcamentos.Where(o => o.ClienteId == clienteId).ToList());

    public Task<IReadOnlyList<Orcamento>> ListarComVersaoVencidaAsync(DateTimeOffset agora, int limite, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Orcamento>>(_orcamentos
            .Where(o => o.VersaoVigente.AguardandoDecisao && o.VersaoVigente.Vencida(agora))
            .Take(limite)
            .ToList());

    public Task<bool> RegistrarAsync(MensagemSaga mensagem, string canal, string payload, CancellationToken ct = default)
        => Task.FromResult(_inbox.Add(mensagem.MessageId));

    public void Adicionar(MensagemSaga mensagem) => _pendentes.Add(mensagem);

    public Task SalvarAsync(CancellationToken ct = default)
    {
        Publicadas.AddRange(_pendentes);
        _pendentes.Clear();
        Gravacoes++;
        return Task.CompletedTask;
    }

    public void DescartarAlteracoes() => _pendentes.Clear();

    public Task<T> ExecutarAsync<T>(Func<Task<T>> acao, CancellationToken ct = default) => acao();
}

public sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = agora;

    public override DateTimeOffset GetUtcNow() => Agora;
}
