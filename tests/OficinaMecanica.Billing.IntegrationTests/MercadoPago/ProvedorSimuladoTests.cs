using OficinaMecanica.Billing.Application.Ports.Out;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Pagamentos;

namespace OficinaMecanica.Billing.IntegrationTests.MercadoPago;

// O simulador reinicia sem memória, mas os pagamentos continuam no banco: a referência nova não pode repetir uma
// referência de antes do restart (CARD-42b).
public class ProvedorSimuladoTests
{
    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private static NovaCobranca Cobranca() => new(Guid.NewGuid(), 100m, "BRL", "cliente@cliente.example", "OS teste",
        DateTimeOffset.UtcNow.AddMinutes(30), $"chave-{Guid.NewGuid()}");

    [Fact]
    public async Task DepoisDeReiniciar_ReferenciaNovaNaoRepeteAsAnteriores()
    {
        var subida = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var antes = new ProvedorPagamentoSimulado(new RelogioFixo(subida));
        var referenciasAntigas = new List<string>();
        for (var i = 0; i < 5; i++)
            referenciasAntigas.Add((await antes.CriarAsync(Cobranca())).Valor!.Referencia);

        var depois = new ProvedorPagamentoSimulado(new RelogioFixo(subida.AddSeconds(1)));
        var nova = (await depois.CriarAsync(Cobranca())).Valor!.Referencia;

        Assert.DoesNotContain(nova, referenciasAntigas);
        Assert.True(long.Parse(nova) > referenciasAntigas.Max(long.Parse));
    }
}
