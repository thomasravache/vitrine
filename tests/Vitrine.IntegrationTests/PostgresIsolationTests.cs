using Npgsql;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PostgresIsolationTests(PostgresFixture postgres)
{
    private const string SharedKey = "mesma-chave";

    [Fact]
    [Trait("spec", "SPEC-0003:IT-01")]
    public async Task Dois_testes_paralelos_gravando_a_mesma_chave_em_bancos_isolados_veem_so_os_proprios_dados()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bothWritten = new TwoPartyRendezvous();

        // Dado a PostgresFixture; quando dois testes paralelos gravam e leem a mesma chave, cada um no seu banco
        var results = await Task.WhenAll(
            WriteThenReadAsync("teste-a", bothWritten, cancellationToken),
            WriteThenReadAsync("teste-b", bothWritten, cancellationToken));

        // Então os bancos são distintos e nenhum vê os dados do outro
        Assert.NotEqual(results[0].ConnectionString, results[1].ConnectionString);
        Assert.Equal(["teste-a"], results[0].ValuesSeen);
        Assert.Equal(["teste-b"], results[1].ValuesSeen);
    }

    private async Task<ScenarioResult> WriteThenReadAsync(
        string owner,
        TwoPartyRendezvous bothWritten,
        CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        await using (var create = dataSource.CreateCommand(
            "CREATE TABLE registro (chave text PRIMARY KEY, valor text NOT NULL)"))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = dataSource.CreateCommand("INSERT INTO registro (chave, valor) VALUES ($1, $2)"))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = SharedKey });
            insert.Parameters.Add(new NpgsqlParameter { Value = owner });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // Os dois gravaram antes de qualquer um ler.
        await bothWritten.ArriveAndWaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        var seen = new List<string>();
        await using (var select = dataSource.CreateCommand("SELECT valor FROM registro WHERE chave = $1 ORDER BY valor"))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = SharedKey });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                seen.Add(reader.GetString(0));
            }
        }

        return new ScenarioResult(connectionString, seen);
    }

    private sealed record ScenarioResult(string ConnectionString, IReadOnlyList<string> ValuesSeen);

    private sealed class TwoPartyRendezvous
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAndWaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
            {
                _allArrived.TrySetResult();
            }

            return _allArrived.Task.WaitAsync(timeout, cancellationToken);
        }
    }
}
