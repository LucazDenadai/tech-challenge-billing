using Microsoft.EntityFrameworkCore;
using Npgsql;
using OficinaMecanica.Billing.Infrastructure.Adapters.Out.Persistence;
using Testcontainers.PostgreSql;

namespace OficinaMecanica.Billing.IntegrationTests.Fixtures;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("billing_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.StopAsync();

    // Cada teste recebe um banco novo e vazio, para que migrations e asserções não dependam de ordem de execução.
    public string NovoBancoVazio()
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = $"billing_{Guid.NewGuid():N}"
        };
        return builder.ConnectionString;
    }

    public async Task<string> NovoBancoMigradoAsync()
    {
        var connectionString = NovoBancoVazio();
        await using var db = CriarContexto(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public static AppDbContext CriarContexto(string connectionString)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
}

[CollectionDefinition(Nome)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Nome = "Postgres";
}
