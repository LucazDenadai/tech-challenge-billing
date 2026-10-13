using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.Out.Messaging;

// Lê um lote de eventos pendentes da outbox do PostgreSQL, publica e marca; espera o intervalo quando não há mais.
// Entrega pelo menos uma vez: se cair entre publicar e marcar, publica de novo; o OS deduplica por messageId.
public class DespachanteOutboxHostedService(
    IServiceScopeFactory scopeFactory,
    PublicadorRabbitMq publicador,
    IOptions<RabbitMqOptions> options,
    ILogger<DespachanteOutboxHostedService> logger) : BackgroundService
{
    private const int Lote = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMilliseconds(options.Value.IntervaloOutboxMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DespacharAsync(stoppingToken) == Lote)
                    continue; // há mais pendentes: segue sem esperar
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Falha ao despachar a outbox; nova tentativa em {Espera}s.", 5);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            await Task.Delay(intervalo, stoppingToken);
        }
    }

    // Retorna quantas mensagens pendentes foram lidas.
    public async Task<int> DespacharAsync(CancellationToken ct)
    {
        using var escopo = scopeFactory.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        // Com mais de uma réplica (CARD-41b), cada despachante trava as linhas que leu até o commit; SKIP LOCKED
        // faz o outro pular as travadas e pegar as próximas. Assim, o mesmo evento não é publicado em paralelo.
        await using var transacao = await db.Database.BeginTransactionAsync(ct);
        var pendentes = await db.Outbox
            .FromSqlRaw("""
                SELECT * FROM "OutboxMensagens"
                WHERE "PublicadaEmUtc" IS NULL
                ORDER BY "CriadaEmUtc"
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
                """, Lote)
            .ToListAsync(ct);

        foreach (var mensagem in pendentes)
        {
            try
            {
                await publicador.PublicarAsync(new MensagemParaPublicar(mensagem.MessageId, mensagem.MessageType, mensagem.Canal,
                    mensagem.CorrelationId, mensagem.Payload, mensagem.TraceParent), ct);
                mensagem.PublicadaEmUtc = DateTimeOffset.UtcNow;
                mensagem.UltimoErro = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                mensagem.Tentativas++;
                mensagem.UltimoErro = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                await db.SaveChangesAsync(ct);
                await transacao.CommitAsync(ct);
                throw;
            }

            await db.SaveChangesAsync(ct);
        }

        await transacao.CommitAsync(ct);
        return pendentes.Count;
    }
}
