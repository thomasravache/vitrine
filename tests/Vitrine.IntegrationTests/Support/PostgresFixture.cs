using Npgsql;
using Testcontainers.PostgreSql;

namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// PostgreSQL real (Testcontainers) compartilhado por uma coleção de testes; cada teste pede um banco
/// próprio e descartável com <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Mesma imagem que o AppHost usa no desenvolvimento (Aspire.Hosting.PostgreSQL 13.6.1).
    private const string Image = "postgres:18.3";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <summary>Cria um banco vazio e isolado e devolve a connection string dele.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            // Nome gerado aqui (só hexadecimal): não há entrada externa no comando.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }
            .ConnectionString;
    }

    /// <summary>
    /// Fecha as conexões ociosas que o pool do Npgsql mantém para <paramref name="connectionString"/> (SPEC-0016).
    /// Chame ao descartar um host próprio que usou um banco de <see cref="CreateDatabaseAsync"/>, para a suíte não
    /// esgotar o limite de clientes do container; o <see cref="ApiFactory"/> já faz isso no próprio descarte.
    /// Só o pool dessa connection string é limpo: os bancos dos outros testes não são afetados.
    /// </summary>
    public static void ReleaseConnections(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    // Os bancos criados por teste somem junto com o container.
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>Coleção xUnit que compartilha um único container PostgreSQL.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
