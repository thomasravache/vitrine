using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Vitrine.Identity.IntegrationEvents;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

/// <summary>Contrato público <c>WorkspaceCreated</c> v1 do módulo Identity, visto por um assinante.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class WorkspaceCreatedContractTests(PostgresFixture postgres)
{
    /// <summary>Schema v1 (SPEC-0006): campo JSON → tipo .NET no contrato publicado em Identity.IntegrationEvents.</summary>
    private static readonly Dictionary<string, Type> SchemaV1 = new(StringComparer.Ordinal)
    {
        ["workspaceId"] = typeof(Guid),
        ["ownerUserId"] = typeof(Guid),
        ["createdAt"] = typeof(DateTimeOffset),
    };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("spec", "SPEC-0006:CT-01")]
    public async Task Assinante_recebe_WorkspaceCreated_v1_com_os_campos_do_contrato()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // O tipo publicado em Identity.IntegrationEvents é exatamente o schema v1
        var contract = typeof(WorkspaceCreated);
        Assert.Equal("Vitrine.Identity.IntegrationEvents", contract.Namespace);
        var properties = contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => property.PropertyType, StringComparer.Ordinal);
        Assert.Equal(SchemaV1.OrderBy(field => field.Key, StringComparer.Ordinal), properties.OrderBy(field => field.Key, StringComparer.Ordinal));

        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);

        // Dado um cadastro concluído
        var before = DateTimeOffset.UtcNow;
        var account = await api.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);
        var after = DateTimeOffset.UtcNow;

        // Quando o handler de teste assinante de WorkspaceCreated recebe a mensagem
        await Eventually.TrueAsync(
            () => WorkspaceCreatedTestHandler.ReceivedFor(account.WorkspaceId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu WorkspaceCreated do workspace cadastrado",
            cancellationToken);
        var message = Assert.Single(WorkspaceCreatedTestHandler.ReceivedFor(account.WorkspaceId));

        // Então o payload valida contra o schema v1: exatamente workspaceId, ownerUserId e createdAt, com os valores do cadastro
        var payload = JsonSerializer.SerializeToElement(message, WebJson);
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(
            SchemaV1.Keys.Order(StringComparer.Ordinal),
            payload.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));

        Assert.True(Guid.TryParse(payload.GetProperty("workspaceId").GetString(), out var workspaceId), "workspaceId não é UUID.");
        Assert.True(Guid.TryParse(payload.GetProperty("ownerUserId").GetString(), out var ownerUserId), "ownerUserId não é UUID.");
        Assert.True(
            DateTimeOffset.TryParseExact(
                payload.GetProperty("createdAt").GetString(),
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var createdAt),
            $"createdAt não é data-hora ISO 8601 com fuso: {payload.GetProperty("createdAt")}.");

        Assert.Equal(account.WorkspaceId, workspaceId);
        Assert.Equal(account.UserId, ownerUserId);
        Assert.InRange(createdAt, before.AddSeconds(-1), after.AddSeconds(1));
    }
}
