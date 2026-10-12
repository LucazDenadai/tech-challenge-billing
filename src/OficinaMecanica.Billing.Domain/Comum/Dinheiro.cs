using OficinaMecanica.Billing.Domain.Orcamentos;

namespace OficinaMecanica.Billing.Domain.Comum;

// Regras do schema Money do AsyncAPI: positivo e com no máximo duas casas decimais.
// O Mercado Pago no Brasil cobra em reais, então o orçamento só aceita BRL.
public static class Dinheiro
{
    public const string Moeda = "BRL";

    public static decimal Validar(decimal valor, string campo)
    {
        if (valor <= 0)
            throw new OrcamentoInvalidoException($"{campo}: o preço deve ser maior que zero.");
        if (decimal.Round(valor, 2) != valor)
            throw new OrcamentoInvalidoException($"{campo}: o preço deve ter no máximo duas casas decimais.");
        return valor;
    }
}
