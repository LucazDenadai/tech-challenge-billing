using System.Security.Cryptography;
using System.Text;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.In.Webhook;

public enum ResultadoAssinatura
{
    Valida,
    Ausente,
    Malformada,
    // Timestamp fora da tolerância configurada.
    Expirada,
    Invalida
}

// Valida o cabeçalho x-signature do webhook do Mercado Pago (ADR-019):
//   x-signature: ts=<epoch em segundos>,v1=<HMAC-SHA256 em hexadecimal>
//   manifesto:   id:<data.id da query, em minúsculas>;request-id:<x-request-id>;ts:<ts>;
// Campo ausente na notificação sai do manifesto, como pede a documentação do Mercado Pago.
public sealed class ValidadorAssinaturaMercadoPago(string segredo, TimeSpan tolerancia, TimeProvider relogio)
{
    public ResultadoAssinatura Validar(string? cabecalhoAssinatura, string? requestId, string? dataId)
    {
        if (string.IsNullOrWhiteSpace(segredo) || string.IsNullOrWhiteSpace(cabecalhoAssinatura))
            return ResultadoAssinatura.Ausente;

        string? ts = null, v1 = null;
        foreach (var parte in cabecalhoAssinatura.Split(','))
        {
            var chaveValor = parte.Split('=', 2, StringSplitOptions.TrimEntries);
            if (chaveValor.Length != 2) continue;
            if (chaveValor[0] == "ts") ts = chaveValor[1];
            else if (chaveValor[0] == "v1") v1 = chaveValor[1];
        }

        if (ts is null || v1 is null || !long.TryParse(ts, out var segundos))
            return ResultadoAssinatura.Malformada;

        byte[] recebido;
        try
        {
            recebido = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return ResultadoAssinatura.Malformada;
        }

        var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(Manifesto(dataId, requestId, ts)));
        // Comparação em tempo constante: o tempo de resposta não revela quantos bytes acertaram.
        if (!CryptographicOperations.FixedTimeEquals(esperado, recebido))
            return ResultadoAssinatura.Invalida;

        // O exemplo da documentação usa segundos. Valor grande demais para segundos é lido como milissegundos, por segurança.
        var instante = segundos > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(segundos) : DateTimeOffset.FromUnixTimeSeconds(segundos);
        return (relogio.GetUtcNow() - instante).Duration() > tolerancia ? ResultadoAssinatura.Expirada : ResultadoAssinatura.Valida;
    }

    public static string Manifesto(string? dataId, string? requestId, string ts)
    {
        var manifesto = new StringBuilder();
        if (!string.IsNullOrEmpty(dataId)) manifesto.Append($"id:{dataId.ToLowerInvariant()};");
        if (!string.IsNullOrEmpty(requestId)) manifesto.Append($"request-id:{requestId};");
        manifesto.Append($"ts:{ts};");
        return manifesto.ToString();
    }

    // Usado pelos testes e pelo simulador para produzir uma notificação assinada como o Mercado Pago.
    public static string Assinar(string segredo, string? dataId, string? requestId, long ts)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(Manifesto(dataId, requestId, ts.ToString())));
        return $"ts={ts},v1={Convert.ToHexStringLower(hash)}";
    }
}
