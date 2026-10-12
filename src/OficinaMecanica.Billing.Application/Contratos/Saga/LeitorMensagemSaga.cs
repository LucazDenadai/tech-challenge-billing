using System.Text.Json;
using System.Text.Json.Serialization;

namespace OficinaMecanica.Billing.Application.Contratos.Saga;

public sealed record LeituraMensagem(MensagemSaga? Mensagem, string? Motivo, Guid? MessageId, Guid? CorrelationId, string? MessageType)
{
    public bool Valida => Mensagem is not null;
}

// Lê o corpo recebido de um canal e o confronta com o contrato desse canal.
// Campos desconhecidos são ignorados: campo opcional novo é mudança compatível e mantém a versão (ADR-018).
public static class LeitorMensagemSaga
{
    private static readonly JsonSerializerOptions _opcoes = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static LeituraMensagem Ler(CanalConsumido canal, ReadOnlyMemory<byte> corpo)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(corpo);
        }
        catch (JsonException)
        {
            return Rejeitar("Corpo não é um JSON válido.");
        }

        using (documento)
        {
            if (documento.RootElement.ValueKind != JsonValueKind.Object)
                return Rejeitar("Corpo não é um objeto JSON.");

            // Leitura tolerante dos identificadores, para que a DLQ e os logs carreguem a referência mesmo quando o contrato falha.
            var raiz = documento.RootElement;
            var messageId = LerGuid(raiz, "messageId");
            var correlationId = LerGuid(raiz, "correlationId");
            var messageType = raiz.TryGetProperty("messageType", out var tipo) && tipo.ValueKind == JsonValueKind.String
                ? tipo.GetString()
                : null;

            LeituraMensagem Falha(string motivo) => new(null, motivo, messageId, correlationId, messageType);

            if (messageType != canal.MessageType)
                return Falha($"messageType '{messageType}' não corresponde ao canal '{canal.Endereco}' (esperado '{canal.MessageType}').");

            MensagemSaga? mensagem;
            try
            {
                mensagem = (MensagemSaga?)raiz.Deserialize(canal.TipoMensagem, _opcoes);
            }
            catch (JsonException ex)
            {
                return Falha($"Payload fora do contrato {canal.MessageType}: {ex.Message}");
            }

            if (mensagem is null)
                return Falha("Payload vazio.");

            if (mensagem.SchemaVersion != canal.SchemaVersion)
                return Falha($"schemaVersion {mensagem.SchemaVersion} não suportado no canal '{canal.Endereco}' (esperado {canal.SchemaVersion}).");

            if (mensagem.Producer != canal.Produtor)
                return Falha($"producer '{mensagem.Producer}' não é o produtor do canal '{canal.Endereco}' (esperado '{canal.Produtor}').");

            var erros = mensagem.Validar();
            if (erros.Count > 0)
                return Falha($"Payload fora do contrato {canal.MessageType}: {string.Join(" ", erros)}");

            return new LeituraMensagem(mensagem, null, mensagem.MessageId, mensagem.CorrelationId, mensagem.MessageType);
        }

        static LeituraMensagem Rejeitar(string motivo) => new(null, motivo, null, null, null);
    }

    private static Guid? LerGuid(JsonElement raiz, string propriedade)
        => raiz.TryGetProperty(propriedade, out var valor)
           && valor.ValueKind == JsonValueKind.String
           && Guid.TryParse(valor.GetString(), out var guid)
            ? guid
            : null;
}
