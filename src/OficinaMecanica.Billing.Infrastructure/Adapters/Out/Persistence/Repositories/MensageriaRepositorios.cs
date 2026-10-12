using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Mensageria;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence.Repositories;

public class InboxRepository(AppDbContext context) : IInboxRepository
{
    // ON CONFLICT torna a deduplicação atômica: duas entregas simultâneas do mesmo messageId gravam uma linha só.
    // O comando usa a transação aberta no contexto, então a linha só fica se o efeito também ficar.
    public async Task<bool> RegistrarAsync(MensagemSaga mensagem, string canal, string payload, CancellationToken ct = default)
    {
        var linhas = await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "InboxMensagens"
                ("MessageId", "MessageType", "Canal", "Producer", "CorrelationId", "CausationId",
                 "OsId", "FilialId", "OccurredAtUtc", "RecebidaEmUtc", "Payload")
            VALUES (@messageId, @messageType, @canal, @producer, @correlationId, @causationId,
                    @osId, @filialId, @occurredAtUtc, @recebidaEmUtc, @payload)
            ON CONFLICT ("MessageId") DO NOTHING
            """,
            [
                new NpgsqlParameter("messageId", mensagem.MessageId),
                new NpgsqlParameter("messageType", mensagem.MessageType),
                new NpgsqlParameter("canal", canal),
                new NpgsqlParameter("producer", mensagem.Producer),
                new NpgsqlParameter("correlationId", mensagem.CorrelationId),
                new NpgsqlParameter("causationId", NpgsqlDbType.Uuid) { Value = (object?)mensagem.CausationId ?? DBNull.Value },
                new NpgsqlParameter("osId", mensagem.OsId),
                new NpgsqlParameter("filialId", mensagem.FilialId),
                new NpgsqlParameter("occurredAtUtc", mensagem.OccurredAtUtc.ToUniversalTime()),
                new NpgsqlParameter("recebidaEmUtc", DateTimeOffset.UtcNow),
                new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload }
            ],
            ct);

        return linhas == 1;
    }
}

public class OutboxRepository(AppDbContext context) : IOutbox
{
    public void Adicionar(MensagemSaga mensagem)
    {
        var canal = CatalogoCanaisBilling.Publicado(mensagem.GetType());
        context.Outbox.Add(new MensagemOutbox
        {
            MessageId = mensagem.MessageId,
            MessageType = mensagem.MessageType,
            Canal = canal.Endereco,
            CorrelationId = mensagem.CorrelationId,
            Payload = RespostaSaga.Serializar(mensagem),
            TraceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } atividade ? atividade.Id : null,
            CriadaEmUtc = DateTimeOffset.UtcNow
        });
    }
}

public class Transacao(AppDbContext context) : ITransacao
{
    public async Task<T> ExecutarAsync<T>(Func<Task<T>> acao, CancellationToken ct = default)
    {
        await using var transacao = await context.Database.BeginTransactionAsync(ct);
        var resultado = await acao();
        await transacao.CommitAsync(ct);
        return resultado;
    }
}
