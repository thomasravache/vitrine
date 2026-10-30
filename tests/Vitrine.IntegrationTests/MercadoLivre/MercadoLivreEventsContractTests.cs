using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Contratos públicos <c>MercadoLivreAccountConnected</c> v1 e <c>MercadoLivreAccountDisconnected</c> v1, vistos por
/// um assinante (consumidos pelas SPEC-0021 e SPEC-0022).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MercadoLivreEventsContractTests(PostgresFixture postgres)
{
    /// <summary>Schema v1 de MercadoLivreAccountConnected (seção 6 da SPEC-0020): campo JSON → tipo .NET.</summary>
    private static readonly Dictionary<string, Type> ConnectedV1 = new(StringComparer.Ordinal)
    {
        ["accountId"] = typeof(Guid),
        ["workspaceId"] = typeof(Guid),
        ["mlUserId"] = typeof(long),
        ["siteId"] = typeof(string),
        ["reconnected"] = typeof(bool),
        ["occurredAt"] = typeof(DateTimeOffset),
    };

    /// <summary>Schema v1 de MercadoLivreAccountDisconnected.</summary>
    private static readonly Dictionary<string, Type> DisconnectedV1 = new(StringComparer.Ordinal)
    {
        ["accountId"] = typeof(Guid),
        ["workspaceId"] = typeof(Guid),
        ["mlUserId"] = typeof(long),
        ["occurredAt"] = typeof(DateTimeOffset),
    };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("spec", "SPEC-0020:CT-01")]
    public async Task Assinante_recebe_Connected_e_Disconnected_v1_com_os_campos_e_tipos_do_contrato()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Os tipos publicados em MercadoLivre.IntegrationEvents são exatamente os schemas v1
        AssertPublishedSchema(typeof(MercadoLivreAccountConnected), ConnectedV1);
        AssertPublishedSchema(typeof(MercadoLivreAccountDisconnected), DisconnectedV1);

        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var seller = MlSeller.New();

        // Dado uma conexão e uma desconexão concluídas
        var before = DateTimeOffset.UtcNow;
        var account = await api.ConnectAsync(fake, seller, MlTokens.New(), cancellationToken);
        using (var disconnect = await api.DisconnectAsync(account.AccountId, cancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.NoContent, disconnect.StatusCode);
        }

        var after = DateTimeOffset.UtcNow;

        // Quando o assinante de teste recebe os dois eventos
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountConnected>(account.AccountId).Count > 0
                && MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountDisconnected>(account.AccountId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreAccountConnected e MercadoLivreAccountDisconnected da conta",
            cancellationToken);

        // Então os payloads validam contra os schemas v1 (mlUserId int64), com os valores da conexão
        var connected = AssertPayload(Assert.Single(MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountConnected>(account.AccountId)), ConnectedV1);
        Assert.Equal(account.AccountId, connected.GetProperty("accountId").GetGuid());
        Assert.Equal(owner.WorkspaceId, connected.GetProperty("workspaceId").GetGuid());
        Assert.Equal(seller.Id, connected.GetProperty("mlUserId").GetInt64());
        Assert.Equal("MLB", connected.GetProperty("siteId").GetString());
        Assert.False(connected.GetProperty("reconnected").GetBoolean());
        Assert.InRange(OccurredAt(connected), before.AddSeconds(-1), after.AddSeconds(1));

        var disconnected = AssertPayload(Assert.Single(MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountDisconnected>(account.AccountId)), DisconnectedV1);
        Assert.Equal(account.AccountId, disconnected.GetProperty("accountId").GetGuid());
        Assert.Equal(owner.WorkspaceId, disconnected.GetProperty("workspaceId").GetGuid());
        Assert.Equal(seller.Id, disconnected.GetProperty("mlUserId").GetInt64());
        Assert.InRange(OccurredAt(disconnected), OccurredAt(connected), after.AddSeconds(1));
    }

    internal static void AssertPublishedSchema(Type contract, Dictionary<string, Type> schema)
    {
        Assert.Equal("Vitrine.MercadoLivre.IntegrationEvents", contract.Namespace);
        var properties = contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => property.PropertyType, StringComparer.Ordinal);
        Assert.Equal(schema.OrderBy(field => field.Key, StringComparer.Ordinal), properties.OrderBy(field => field.Key, StringComparer.Ordinal));
    }

    internal static JsonElement AssertPayload(object message, Dictionary<string, Type> schema)
    {
        var payload = JsonSerializer.SerializeToElement(message, message.GetType(), WebJson);
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(schema.Keys.Order(StringComparer.Ordinal), payload.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        foreach (var (name, type) in schema)
        {
            var value = payload.GetProperty(name);
            var valid = type switch
            {
                _ when type == typeof(Guid) => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
                _ when type == typeof(long) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                _ when type == typeof(string) => value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()),
                _ when type == typeof(bool) => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ when type == typeof(DateTimeOffset) => value.ValueKind == JsonValueKind.String && TryParseUtc(value.GetString(), out _),
                _ => false,
            };
            Assert.True(valid, $"Campo {name} não segue o tipo {type.Name} do schema v1: {value}");
        }

        return payload;
    }

    internal static DateTimeOffset OccurredAt(JsonElement payload)
    {
        Assert.True(TryParseUtc(payload.GetProperty("occurredAt").GetString(), out var occurredAt), "occurredAt inválido.");
        return occurredAt;
    }

    // datetime(UTC): ISO 8601 com deslocamento zero.
    private static bool TryParseUtc(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
        && value.Offset == TimeSpan.Zero;
}
