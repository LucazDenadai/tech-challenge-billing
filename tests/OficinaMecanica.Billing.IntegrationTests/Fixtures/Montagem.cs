using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Application.UseCases.Mensageria;
using OficinaMecanica.Billing.Application.UseCases.Orcamentos;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;

namespace OficinaMecanica.Billing.IntegrationTests.Fixtures;

// Monta os casos de uso sobre um AppDbContext real, sem a API.
public static class Montagem
{
    public const string CanalPedido = "saga-os.quote-requested.v1";
    public static readonly TimeSpan Validade = TimeSpan.FromHours(24);

    public static ProcessarMensagemSagaUseCase Processar(AppDbContext db, IOutbox? outbox = null, TimeProvider? relogio = null)
        => new(new Transacao(db), new InboxRepository(db), outbox ?? new OutboxRepository(db), new UnidadeDeTrabalho(db),
            new SolicitarOrcamentoUseCase(new OrcamentoRepository(db), new ConfiguracaoOrcamento(Validade), relogio ?? TimeProvider.System),
            NullLogger<ProcessarMensagemSagaUseCase>.Instance);

    public static DecidirOrcamentoUseCase Decidir(AppDbContext db, TimeProvider? relogio = null)
        => new(new OrcamentoRepository(db), new OutboxRepository(db), new UnidadeDeTrabalho(db), relogio ?? TimeProvider.System,
            NullLogger<DecidirOrcamentoUseCase>.Instance);

    public static ExpirarOrcamentosUseCase Expirar(AppDbContext db, TimeProvider relogio)
        => new(new OrcamentoRepository(db), new OutboxRepository(db), new UnidadeDeTrabalho(db), relogio,
            NullLogger<ExpirarOrcamentosUseCase>.Instance);

    // QuoteRequested válido, com itens de peça e serviço (total 125,90).
    public static JsonObject Pedido(Guid? osId = null, Guid? clienteId = null)
    {
        var os = osId ?? Guid.NewGuid();
        return new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString(),
            ["messageType"] = "QuoteRequested.v1",
            ["schemaVersion"] = 1,
            ["producer"] = "os",
            ["occurredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["causationId"] = Guid.NewGuid().ToString(),
            ["osId"] = os.ToString(),
            ["filialId"] = Guid.NewGuid().ToString(),
            ["idempotencyKey"] = $"orcamento:{os}:{Guid.NewGuid():N}",
            ["clienteId"] = (clienteId ?? Guid.NewGuid()).ToString(),
            ["priceSnapshotAtUtc"] = "2026-10-12T09:00:00-03:00",
            ["currency"] = "BRL",
            ["items"] = new JsonArray(
                new JsonObject { ["itemId"] = Guid.NewGuid().ToString(), ["type"] = "Peca", ["description"] = "Filtro de óleo", ["quantity"] = 1, ["unitPrice"] = 45.90m },
                new JsonObject { ["itemId"] = Guid.NewGuid().ToString(), ["type"] = "Servico", ["description"] = "Troca de óleo", ["quantity"] = 1, ["unitPrice"] = 80m })
        };
    }

    public static ReadOnlyMemory<byte> Bytes(JsonNode mensagem) => Encoding.UTF8.GetBytes(mensagem.ToJsonString());
}

public sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = agora;

    public override DateTimeOffset GetUtcNow() => Agora;
}
