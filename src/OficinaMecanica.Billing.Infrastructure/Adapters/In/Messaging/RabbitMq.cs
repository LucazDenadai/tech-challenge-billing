using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace OficinaMecanica.Billing.Infrastructure.Adapters.In.Messaging;

public class RabbitMqOptions
{
    public const string Secao = "RabbitMq";

    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public ushort PrefetchCount { get; set; } = 10;
    // Intervalo de leitura da outbox pelo despachante.
    public int IntervaloOutboxMs { get; set; } = 500;

    public ConnectionFactory CriarFabrica(string nomeCliente) => new()
    {
        HostName = Host,
        Port = Port,
        VirtualHost = VirtualHost,
        UserName = Username,
        Password = Password,
        ClientProvidedName = nomeCliente,
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true
    };
}

public static class TelemetriaMensageria
{
    public const string NomeFonte = "OficinaMecanica.Billing.Mensageria";

    public static readonly ActivitySource Fonte = new(NomeFonte);
}

public class EstadoConsumidorSaga
{
    private volatile bool _consumindo;

    public bool Consumindo => _consumindo;

    public void Marcar(bool consumindo) => _consumindo = consumindo;
}

public class ConsumidorSagaHealthCheck(EstadoConsumidorSaga estado) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(estado.Consumindo
            ? HealthCheckResult.Healthy("Consumindo os canais da Saga.")
            : HealthCheckResult.Unhealthy("Sem conexão com o RabbitMQ."));
}
