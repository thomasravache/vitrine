namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// PostgreSQL real (Testcontainers) compartilhado por uma coleção de testes; cada teste pede um banco
/// próprio e descartável com <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Cria um banco vazio e isolado e devolve a connection string dele.</summary>
    public Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("SPEC-0003: PostgresFixture (Testcontainers) ainda não implementada.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Coleção xUnit que compartilha um único container PostgreSQL.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
