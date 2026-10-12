using System.Text.Json.Nodes;
using OficinaMecanica.Billing.Application.Contratos.Saga;
using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.UnitTests.Contratos;

// Testes de contrato do ADR-018: os DTOs, o validador e os eventos publicados por Billing concordam
// com o JSON Schema da spec AsyncAPI.
public class ContratoAsyncApiTests
{
    // Canais de Billing na spec que entram no CARD-39b (pagamento e estorno).
    private static readonly string[] _consumidosPendentes39b =
        ["saga-os.payment-creation-requested.v1", "saga-os.payment-refund-requested.v1"];

    private static readonly string[] _publicadosPendentes39b =
    [
        "saga-os.payment-pending.v1", "saga-os.payment-approved.v1", "saga-os.payment-declined.v1", "saga-os.payment-outcome-unknown.v1",
        "saga-os.payment-refunded.v1", "saga-os.payment-refund-pending.v1", "saga-os.payment-refund-failed.v1"
    ];

    public static TheoryData<string> Canais()
    {
        var dados = new TheoryData<string>();
        foreach (var canal in CatalogoCanaisBilling.Consumidos)
            dados.Add(canal.Endereco);
        return dados;
    }

    // Mutações que impedem identificar o pedido: spec e validador rejeitam (a mensagem vai para a DLQ).
    private static readonly (string Descricao, Func<JsonObject, bool> Aplicar)[] _mutacoesDeContrato =
    [
        ("messageId não é UUID", m => Trocar(m, "messageId", "nao-e-uuid")),
        ("occurredAtUtc não é data", m => Trocar(m, "occurredAtUtc", "ontem")),
        ("schemaVersion zero", m => Trocar(m, "schemaVersion", 0)),
        ("producer desconhecido", m => Trocar(m, "producer", "estoque")),
        ("osId numérico", m => Trocar(m, "osId", 123)),
        ("idempotencyKey vazia", m => Trocar(m, "idempotencyKey", "")),
        ("clienteId não é UUID", m => Trocar(m, "clienteId", "cliente")),
        ("item com tipo desconhecido", m => TrocarNoItem(m, "type", "Brinde")),
        ("item com itemId inválido", m => TrocarNoItem(m, "itemId", "item")),
        ("item sem descrição", m => TrocarNoItem(m, "description", "")),
        ("item nulo", m => m["items"] is JsonArray itens && Substituir(itens))
    ];

    // Mutações de negócio: a spec rejeita, mas Billing lê o pedido e responde QuoteRejected (ADR-017),
    // para que o OS receba uma resposta em vez de perder a mensagem na DLQ.
    public static TheoryData<string> MutacoesDeNegocio() => new()
    {
        "items vazio", "item sem preço", "preço zero", "preço com três casas", "quantidade zero", "sem moeda"
    };

    private static bool AplicarNegocio(string caso, JsonObject m) => caso switch
    {
        "items vazio" => Trocar(m, "items", new JsonArray()),
        "item sem preço" => ((JsonObject)m["items"]![0]!).Remove("unitPrice"),
        "preço zero" => TrocarNoItem(m, "unitPrice", 0),
        "preço com três casas" => TrocarNoItem(m, "unitPrice", 10.123m),
        "quantidade zero" => TrocarNoItem(m, "quantity", 0),
        "sem moeda" => m.Remove("currency"),
        _ => throw new ArgumentOutOfRangeException(nameof(caso))
    };

    // Campos obrigatórios na spec cuja ausência Billing trata como recusa de negócio.
    private static readonly string[] _obrigatoriosTratadosComoNegocio = ["currency"];

    [Fact]
    public void Consumidos_MaisPendentesDo39b_CorrespondemAosCanaisDaSpecCujoConsumidorEBilling()
    {
        var spec = SpecAsyncApi.CanaisConsumidosPorBilling();

        Assert.Equal(spec.Keys.Order(), CatalogoCanaisBilling.Consumidos.Select(c => c.Endereco).Concat(_consumidosPendentes39b).Order());
        foreach (var canal in CatalogoCanaisBilling.Consumidos)
            Assert.Equal(SpecAsyncApi.NomeVersionado(spec[canal.Endereco]), canal.MessageType);
    }

    [Fact]
    public void Publicados_MaisPendentesDo39b_CorrespondemAosCanaisDaSpecCujoProdutorEBilling()
    {
        var spec = SpecAsyncApi.CanaisProduzidosPorBilling();

        Assert.Equal(spec.Keys.Order(), CatalogoCanaisBilling.Publicados.Select(c => c.Endereco).Concat(_publicadosPendentes39b).Order());
        foreach (var canal in CatalogoCanaisBilling.Publicados)
            Assert.Equal(SpecAsyncApi.NomeVersionado(spec[canal.Endereco]), canal.MessageType);
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void ExemploValido_AceitoPelaSpecEPeloValidador(string endereco)
    {
        var canal = CatalogoCanaisBilling.ObterConsumido(endereco)!;
        var exemplo = MensagensExemplo.Criar(canal);

        Assert.True(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo), "A spec rejeitou o exemplo; ajuste MensagensExemplo.");
        var leitura = LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo));
        Assert.True(leitura.Valida, leitura.Motivo);
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void CampoDesconhecido_AceitoPelaSpecEPeloValidador(string endereco)
    {
        // Campo opcional novo é mudança compatível (ADR-018): o consumidor não pode rejeitar.
        var canal = CatalogoCanaisBilling.ObterConsumido(endereco)!;
        var exemplo = MensagensExemplo.Criar(canal);
        exemplo["campoNovoOpcional"] = "valor";

        Assert.True(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo));
        Assert.True(LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida);
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void CadaCampoObrigatorioDaSpec_AusenteERejeitadoPeloValidador(string endereco)
    {
        var canal = CatalogoCanaisBilling.ObterConsumido(endereco)!;
        var aceitos = new List<string>();

        foreach (var campo in SpecAsyncApi.CamposObrigatorios(NomeNaSpec(canal)).Except(_obrigatoriosTratadosComoNegocio))
        {
            var exemplo = MensagensExemplo.Criar(canal);
            exemplo.Remove(campo);

            Assert.False(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo), $"Spec aceitou mensagem sem '{campo}'.");
            if (LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida)
                aceitos.Add(campo);
        }

        Assert.True(aceitos.Count == 0, $"Validador aceitou mensagem sem: {string.Join(", ", aceitos)}");
    }

    [Theory]
    [MemberData(nameof(Canais))]
    public void MutacaoDeContrato_RejeitadaPelaSpecEPeloValidador(string endereco)
    {
        var canal = CatalogoCanaisBilling.ObterConsumido(endereco)!;
        var divergencias = new List<string>();

        foreach (var (descricao, aplicar) in _mutacoesDeContrato)
        {
            var exemplo = MensagensExemplo.Criar(canal);
            if (!aplicar(exemplo)) continue;

            var spec = SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo);
            var validador = LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida;
            if (spec || validador)
                divergencias.Add($"{descricao} (spec aceitou: {spec}, validador aceitou: {validador})");
        }

        Assert.True(divergencias.Count == 0, string.Join("; ", divergencias));
    }

    [Theory]
    [MemberData(nameof(MutacoesDeNegocio))]
    public void MutacaoDeNegocio_ForaDaSpecMasLidaParaVirarQuoteRejected(string caso)
    {
        var canal = CatalogoCanaisBilling.ObterConsumido("saga-os.quote-requested.v1")!;
        var exemplo = MensagensExemplo.Criar(canal);
        Assert.True(AplicarNegocio(caso, exemplo));

        Assert.False(SpecAsyncApi.Valida(NomeNaSpec(canal), exemplo), $"A spec aceitou '{caso}'.");
        var leitura = LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo));
        Assert.True(leitura.Valida, leitura.Motivo);
    }

    [Fact]
    public void MensagemDeOutroProdutor_RejeitadaPeloValidador()
    {
        // O producer é válido na spec, mas não é o produtor do canal: só o OS comanda Billing.
        var canal = CatalogoCanaisBilling.Consumidos[0];
        var exemplo = MensagensExemplo.Criar(canal);
        exemplo["producer"] = "operacoes";

        Assert.False(LeitorMensagemSaga.Ler(canal, MensagensExemplo.Bytes(exemplo)).Valida);
    }

    public static TheoryData<string> EventosPublicados() => new() { "pronto", "recusado", "aprovado", "recusado-pelo-cliente", "expirado" };

    [Theory]
    [MemberData(nameof(EventosPublicados))]
    public void EventoPublicado_SerializadoConformeASpec(string caso)
    {
        var canalPedido = CatalogoCanaisBilling.Consumidos[0];
        var pedido = (QuoteRequested)LeitorMensagemSaga.Ler(canalPedido, MensagensExemplo.Bytes(MensagensExemplo.Criar(canalPedido))).Mensagem!;
        var agora = DateTimeOffset.UtcNow;
        var orcamento = Orcamento.Criar(pedido.OsId, pedido.ClienteId, pedido.FilialId, pedido.CorrelationId, pedido.IdempotencyKey!, "BRL",
            pedido.PriceSnapshotAtUtc, [new ItemPrecificado(Guid.NewGuid(), TipoItemOrcamento.Peca, "Filtro de óleo", 2, 45.90m)], agora, TimeSpan.FromHours(24));

        MensagemSaga evento = caso switch
        {
            "pronto" => RespostaSaga.Pronto(OrigemEvento.De(pedido), orcamento.Id, orcamento.VersaoVigente),
            "recusado" => RespostaSaga.Recusado(OrigemEvento.De(pedido), "Item 1 sem preço."),
            "aprovado" => Decidido(orcamento, ResultadoDecisao.Aprovado, agora),
            "recusado-pelo-cliente" => Decidido(orcamento, ResultadoDecisao.Recusado, agora),
            "expirado" => RespostaSaga.Expirado(orcamento, orcamento.ExpirarSeVencido(agora.AddDays(2))!),
            _ => throw new ArgumentOutOfRangeException(nameof(caso))
        };

        var json = JsonNode.Parse(RespostaSaga.Serializar(evento))!;
        var canal = CatalogoCanaisBilling.Publicado(evento.GetType());
        var nome = SpecAsyncApi.CanaisProduzidosPorBilling()[canal.Endereco];

        Assert.True(SpecAsyncApi.Valida(nome, json), $"A spec rejeitou {evento.MessageType}: {json.ToJsonString()}");
        Assert.Equal("billing", json["producer"]!.GetValue<string>());
        Assert.Equal(pedido.CorrelationId.ToString(), json["correlationId"]!.GetValue<string>());
        Assert.Equal(pedido.OsId.ToString(), json["osId"]!.GetValue<string>());
        Assert.Null(json["idempotencyKey"]);
        // Só a resposta ao comando tem causationId; decisão e expiração não vêm de uma mensagem.
        Assert.Equal(caso is "pronto" or "recusado", json["causationId"] is not null);
    }

    private static QuoteDecision Decidido(Orcamento orcamento, ResultadoDecisao resultado, DateTimeOffset agora)
    {
        var tentativa = orcamento.Decidir(1, resultado, orcamento.ClienteId, null, agora);
        return RespostaSaga.Decidido(orcamento, tentativa.Versao!, agora);
    }

    private static string NomeNaSpec(CanalConsumido canal) => SpecAsyncApi.CanaisConsumidosPorBilling()[canal.Endereco];

    private static bool Trocar(JsonObject mensagem, string campo, JsonNode? valor)
    {
        if (!mensagem.ContainsKey(campo)) return false;
        mensagem[campo] = valor;
        return true;
    }

    private static bool TrocarNoItem(JsonObject mensagem, string campo, JsonNode valor)
    {
        if (mensagem["items"] is not JsonArray { Count: > 0 } itens || itens[0] is not JsonObject item || !item.ContainsKey(campo))
            return false;
        item[campo] = valor;
        return true;
    }

    private static bool Substituir(JsonArray itens)
    {
        itens.Clear();
        itens.Add(null);
        return true;
    }
}
