using System.Text;
using System.Text.Json.Nodes;
using OficinaMecanica.Billing.Application.Contratos.Saga;

namespace OficinaMecanica.Billing.UnitTests.Contratos;

// Comandos válidos de exemplo, um por canal consumido por Billing. Sem dados pessoais.
public static class MensagensExemplo
{
    private const string Instante = "2026-10-12T12:00:00Z";

    public static JsonObject Criar(CanalConsumido canal)
    {
        var mensagem = new JsonObject
        {
            ["messageId"] = Guid.NewGuid().ToString(),
            ["messageType"] = canal.MessageType,
            ["schemaVersion"] = canal.SchemaVersion,
            ["producer"] = canal.Produtor,
            ["occurredAtUtc"] = Instante,
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["causationId"] = Guid.NewGuid().ToString(),
            ["osId"] = Guid.NewGuid().ToString(),
            ["filialId"] = Guid.NewGuid().ToString(),
            ["idempotencyKey"] = $"exemplo:{Guid.NewGuid():N}"
        };

        foreach (var (campo, valor) in Payload(canal.TipoMensagem.Name))
            mensagem[campo] = valor;

        return mensagem;
    }

    public static ReadOnlyMemory<byte> Bytes(JsonNode mensagem) => Encoding.UTF8.GetBytes(mensagem.ToJsonString());

    private static IEnumerable<(string, JsonNode)> Payload(string tipo) => tipo switch
    {
        nameof(QuoteRequested) =>
        [
            ("clienteId", Id()),
            ("priceSnapshotAtUtc", Instante),
            ("currency", "BRL"),
            ("items", new JsonArray(
                new JsonObject { ["itemId"] = Id(), ["type"] = "Peca", ["description"] = "Filtro de óleo", ["quantity"] = 1, ["unitPrice"] = 45.90m },
                new JsonObject { ["itemId"] = Id(), ["type"] = "Servico", ["description"] = "Troca de óleo", ["quantity"] = 1, ["unitPrice"] = 80m }))
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Sem exemplo para o tipo.")
    };

    private static JsonNode Id() => Guid.NewGuid().ToString();
}
