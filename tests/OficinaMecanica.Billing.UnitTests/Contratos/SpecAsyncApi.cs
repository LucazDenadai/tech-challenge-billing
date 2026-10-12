using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Serialization;

namespace OficinaMecanica.Billing.UnitTests.Contratos;

// Lê a cópia da spec AsyncAPI (contratos/asyncapi-saga-os.yaml) e expõe o JSON Schema de cada mensagem,
// para que os testes de contrato confrontem o código com a spec, e não com uma cópia escrita à mão (ADR-018).
public static class SpecAsyncApi
{
    private const string Prefixo = "#/components/messages/";

    private static readonly Lazy<JsonObject> _documento = new(Carregar);

    public static JsonObject Documento => _documento.Value;

    // Canais em que Billing aparece como consumidor ou produtor na descrição: endereço → nome da mensagem.
    public static IReadOnlyDictionary<string, string> CanaisConsumidosPorBilling() => Canais("Consumidor: Billing");

    public static IReadOnlyDictionary<string, string> CanaisProduzidosPorBilling() => Canais("Produtor: Billing");

    private static IReadOnlyDictionary<string, string> Canais(string trecho)
        => Documento["channels"]!.AsObject()
            .Where(c => c.Value!["description"]!.GetValue<string>().Contains(trecho))
            .ToDictionary(
                c => c.Value!["address"]!.GetValue<string>(),
                c => c.Value!["messages"]!.AsObject().Single().Value!["$ref"]!.GetValue<string>()[Prefixo.Length..]);

    public static string NomeVersionado(string mensagem)
        => Documento["components"]!["messages"]![mensagem]!["name"]!.GetValue<string>();

    // Schema do payload com os components/schemas embutidos como $defs, para resolver os $ref localmente.
    public static JsonSchema SchemaDoPayload(string mensagem)
    {
        var payload = Documento["components"]!["messages"]![mensagem]!["payload"]!.DeepClone().AsObject();
        payload["$defs"] = Documento["components"]!["schemas"]!.DeepClone();
        var texto = payload.ToJsonString().Replace("#/components/schemas/", "#/$defs/");
        return JsonSchema.FromText(texto);
    }

    public static bool Valida(string mensagem, JsonNode instancia)
    {
        var opcoes = new EvaluationOptions { RequireFormatValidation = true };
        using var documento = JsonDocument.Parse(instancia.ToJsonString());
        return SchemaDoPayload(mensagem).Evaluate(documento.RootElement, opcoes).IsValid;
    }

    // Campos obrigatórios no nível raiz do payload, somando os `required` de cada ramo do allOf.
    public static IReadOnlySet<string> CamposObrigatorios(string mensagem)
    {
        var campos = new HashSet<string>();
        Coletar(Documento["components"]!["messages"]![mensagem]!["payload"]!, campos);
        return campos;
    }

    private static void Coletar(JsonNode no, HashSet<string> campos)
    {
        if (no["$ref"] is JsonValue referencia)
        {
            var nome = referencia.GetValue<string>()["#/components/schemas/".Length..];
            Coletar(Documento["components"]!["schemas"]![nome]!, campos);
        }

        if (no["required"] is JsonArray obrigatorios)
            foreach (var campo in obrigatorios)
                campos.Add(campo!.GetValue<string>());

        if (no["allOf"] is JsonArray ramos)
            foreach (var ramo in ramos)
                Coletar(ramo!, campos);
    }

    private static JsonObject Carregar()
    {
        var caminho = Path.Combine(AppContext.BaseDirectory, "Contratos", "asyncapi-saga-os.yaml");
        var yaml = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build()
            .Deserialize<object>(File.ReadAllText(caminho));

        return JsonSerializer.SerializeToNode(Normalizar(yaml))!.AsObject();
    }

    // YamlDotNet devolve Dictionary<object, object>; o JSON precisa de chaves string.
    private static object? Normalizar(object? valor) => valor switch
    {
        IDictionary<object, object> mapa => mapa.ToDictionary(kv => kv.Key.ToString()!, kv => Normalizar(kv.Value)),
        IList<object> lista => lista.Select(Normalizar).ToList(),
        _ => valor
    };
}
