using OficinaMecanica.Billing.Infrastructure.Adapters.In.Webhook;
using OficinaMecanica.Billing.IntegrationTests.Fixtures;

namespace OficinaMecanica.Billing.IntegrationTests.MercadoPago;

// x-signature do webhook (ADR-019). O hash esperado foi calculado fora do código (Python, hmac/sha256),
// para que o teste não confira a implementação contra ela mesma.
public class ValidadorAssinaturaTests
{
    private const string Segredo = "segredo-de-teste";
    private const string RequestId = "bb56a2f1-6aae-46ac-982e-9dcd3581d08e";
    private const long Ts = 1760356800;
    private const string HashEsperado = "2940835e0bc68b0b96986287f01262cbca5bcfd87cf5bb6d0d71011ffe51fa02";

    private readonly RelogioFixo _relogio = new(DateTimeOffset.FromUnixTimeSeconds(Ts).AddMinutes(1));

    private ValidadorAssinaturaMercadoPago Validador(string segredo = Segredo) => new(segredo, TimeSpan.FromMinutes(30), _relogio);

    [Fact]
    public void AssinaturaCalculadaForaDoCodigo_EValida()
        => Assert.Equal(ResultadoAssinatura.Valida, Validador().Validar($"ts={Ts},v1={HashEsperado}", RequestId, "12345abc"));

    [Fact]
    public void DataIdEmMaiusculas_EntraNoManifestoEmMinusculas()
        => Assert.Equal(ResultadoAssinatura.Valida, Validador().Validar($"ts={Ts},v1={HashEsperado}", RequestId, "12345ABC"));

    [Fact]
    public void Assinar_ProduzOMesmoHashDoCalculoExterno()
        => Assert.Equal($"ts={Ts},v1={HashEsperado}", ValidadorAssinaturaMercadoPago.Assinar(Segredo, "12345abc", RequestId, Ts));

    [Theory]
    [InlineData("outro-id", RequestId)]
    [InlineData("12345abc", "outro-request-id")]
    public void RecursoOuRequisicaoTrocados_Invalida(string dataId, string requestId)
        => Assert.Equal(ResultadoAssinatura.Invalida, Validador().Validar($"ts={Ts},v1={HashEsperado}", requestId, dataId));

    [Fact]
    public void OutroSegredo_Invalida()
        => Assert.Equal(ResultadoAssinatura.Invalida, Validador("outro-segredo").Validar($"ts={Ts},v1={HashEsperado}", RequestId, "12345abc"));

    [Fact]
    public void TimestampTrocado_Invalida()
        => Assert.Equal(ResultadoAssinatura.Invalida, Validador().Validar($"ts={Ts + 1},v1={HashEsperado}", RequestId, "12345abc"));

    [Fact]
    public void AssinaturaValidaForaDaTolerancia_Expirada()
    {
        _relogio.Agora = DateTimeOffset.FromUnixTimeSeconds(Ts).AddHours(2);

        Assert.Equal(ResultadoAssinatura.Expirada, Validador().Validar($"ts={Ts},v1={HashEsperado}", RequestId, "12345abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SemCabecalho_Ausente(string? cabecalho)
        => Assert.Equal(ResultadoAssinatura.Ausente, Validador().Validar(cabecalho, RequestId, "12345abc"));

    [Fact]
    public void SemSegredoConfigurado_NuncaAceita()
        => Assert.Equal(ResultadoAssinatura.Ausente, Validador("").Validar($"ts={Ts},v1={HashEsperado}", RequestId, "12345abc"));

    [Theory]
    [InlineData("v1=abc")]
    [InlineData("ts=abc,v1=00")]
    [InlineData("ts=1760356800,v1=nao-e-hex")]
    [InlineData("qualquer coisa")]
    public void CabecalhoMalformado_Malformada(string cabecalho)
        => Assert.Equal(ResultadoAssinatura.Malformada, Validador().Validar(cabecalho, RequestId, "12345abc"));
}
