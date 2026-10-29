using System.Net;
using Npgsql;
using Vitrine.IntegrationTests.Identity.Support;

namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// IT-01 (SPEC-0016): o descarte de um <see cref="ApiFactory"/> libera as conexões do banco dele no PostgreSQL
/// compartilhado (pools do EF Core e do Wolverine), para a suíte não esgotar o limite de clientes do container.
/// A contagem vem de <c>pg_stat_activity</c>, lida por uma conexão administrativa sem pool no banco <c>postgres</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FixtureConnectionTests(PostgresFixture postgres)
{
    // Tolerância para o backend do PostgreSQL sair de pg_stat_activity depois do fechamento da conexão.
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("spec", "SPEC-0016:IT-01")]
    public async Task Descartar_o_ApiFactory_fecha_as_conexoes_do_banco_dele()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um banco criado pelo PostgresFixture e um ApiFactory que atendeu requisições que tocam o banco
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var factory = IdentityHost.Create(connectionString);
        try
        {
            using var client = factory.CreateClient();
            using (var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            }

            using var api = new IdentityApi(factory);
            await api.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);
            Assert.True(
                await OpenConnectionsAsync(connectionString, database, cancellationToken) > 0,
                "O host deveria ter aberto conexões no banco do teste antes do descarte.");
        }
        finally
        {
            // Quando o ApiFactory é descartado
            await factory.DisposeAsync();
        }

        // Então pg_stat_activity não tem conexões para esse banco (polling curto: o backend sai logo após o fechamento)
        var deadline = DateTime.UtcNow + ReleaseTimeout;
        var remaining = await OpenConnectionsAsync(connectionString, database, cancellationToken);
        while (remaining > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            remaining = await OpenConnectionsAsync(connectionString, database, cancellationToken);
        }

        Assert.True(
            remaining == 0,
            $"O banco {database} ainda tem {remaining} conexão(ões) em pg_stat_activity {ReleaseTimeout.TotalSeconds}s depois do descarte do ApiFactory.");
    }

    /// <summary>Conexões abertas para <paramref name="database"/>, contadas por uma conexão administrativa sem pool.</summary>
    private static async Task<int> OpenConnectionsAsync(string connectionString, string database, CancellationToken cancellationToken)
    {
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @database", connection);
        count.Parameters.AddWithValue("database", database);
        return Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }
}
