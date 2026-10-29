using System.Net;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Observability;

/// <summary><c>GET /health/live</c> e <c>GET /health/ready</c> (contrato da SPEC-0007).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    private const string LivePath = "/health/live";
    private const string ReadyPath = "/health/ready";

    // Mesma imagem do PostgresFixture/AppHost.
    private const string PostgresImage = "postgres:18.3";

    // Sinais de detalhe interno que um endpoint de health nunca expõe (exceção, stack trace, connection string).
    private static readonly string[] InternalDetailMarkers =
    [
        "Exception", "   at ", "Npgsql", "Host=", "Server=", "Port=", "Username=", "User ID=", "Password=", "Database=",
        "postgres", "description", "duration", "entries", "data",
    ];

    [Fact]
    [Trait("spec", "SPEC-0007:IT-01")]
    public async Task Live_e_ready_respondem_200_sem_host_connection_string_nem_stack_trace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a Api com PostgreSQL real
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var client = factory.CreateClient();

        // Quando GET /health/live e GET /health/ready (anônimos)
        using var live = await client.GetAsync(new Uri(LivePath, UriKind.Relative), cancellationToken);
        using var ready = await client.GetAsync(new Uri(ReadyPath, UriKind.Relative), cancellationToken);

        // Então ambos 200: live "Healthy"; ready {"status":"Healthy"}
        var liveBody = await AssertStatusAsync(HttpStatusCode.OK, live, cancellationToken);
        Assert.Equal("Healthy", liveBody);
        var readyBody = await AssertStatusAsync(HttpStatusCode.OK, ready, cancellationToken);
        AssertStatusOnlyJson("Healthy", readyBody);

        // E nenhum corpo contém host, connection string nem stack trace
        AssertNoInternalDetails(liveBody, connectionString);
        AssertNoInternalDetails(readyBody, connectionString);
    }

    [Fact]
    [Trait("spec", "SPEC-0007:IT-02")]
    public async Task Com_o_banco_parado_ready_responde_503_sem_detalhes_e_live_continua_200()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a Api sobre um container PostgreSQL próprio (o da coleção não pode ser parado) e pronta
        await using var container = new PostgreSqlBuilder(PostgresImage).Build();
        await container.StartAsync(cancellationToken);
        var connectionString = container.GetConnectionString();
        await using var factory = IdentityHost.Create(connectionString);
        using var client = factory.CreateClient();
        using (var readyBefore = await client.GetAsync(new Uri(ReadyPath, UriKind.Relative), cancellationToken))
        {
            var body = await AssertStatusAsync(HttpStatusCode.OK, readyBefore, cancellationToken);
            AssertStatusOnlyJson("Healthy", body);
        }

        // Quando o container do PostgreSQL é parado
        await container.StopAsync(cancellationToken);

        // Então GET /health/ready responde 503 {"status":"Unhealthy"} sem detalhes internos
        string? unhealthyBody = null;
        await Eventually.TrueAsync(
            async () =>
            {
                using var ready = await client.GetAsync(new Uri(ReadyPath, UriKind.Relative), cancellationToken);
                if (ready.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    return false;
                }

                unhealthyBody = await ready.Content.ReadAsStringAsync(cancellationToken);
                return true;
            },
            TimeSpan.FromSeconds(60),
            "GET /health/ready deveria responder 503 com o banco parado",
            cancellationToken);
        AssertStatusOnlyJson("Unhealthy", unhealthyBody!);
        AssertNoInternalDetails(unhealthyBody!, connectionString);

        // E GET /health/live continua 200 (não depende do banco)
        using var live = await client.GetAsync(new Uri(LivePath, UriKind.Relative), cancellationToken);
        var liveBody = await AssertStatusAsync(HttpStatusCode.OK, live, cancellationToken);
        Assert.Equal("Healthy", liveBody);
    }

    private static async Task<string> AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            response.StatusCode == expected,
            $"GET {response.RequestMessage?.RequestUri?.AbsolutePath}: esperado {(int)expected} {expected}, "
            + $"recebido {(int)response.StatusCode} {response.StatusCode}. Corpo: {body}");
        return body;
    }

    /// <summary>Corpo exatamente <c>{"status":"&lt;status&gt;"}</c>: nenhuma outra propriedade (nada de entradas por checagem).</summary>
    private static void AssertStatusOnlyJson(string expectedStatus, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var properties = root.EnumerateObject().Select(property => property.Name).ToList();
        Assert.True(properties is ["status"], $"Corpo de /health/ready deveria ter só \"status\"; veio: {body}");
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
    }

    private static void AssertNoInternalDetails(string body, string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        string?[] secrets = [connectionString, builder.Host, builder.Database, builder.Username, builder.Password, builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
        {
            Assert.False(body.Contains(secret!, StringComparison.OrdinalIgnoreCase), $"Health expôs \"{secret}\": {body}");
        }

        foreach (var marker in InternalDetailMarkers)
        {
            Assert.False(body.Contains(marker, StringComparison.OrdinalIgnoreCase), $"Health expôs detalhe interno (\"{marker}\"): {body}");
        }
    }
}
