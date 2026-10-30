using System.Globalization;
using System.Text.Json;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.MercadoLivre;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:CT-01 — lado consumidor dos contratos: payloads JSON de exemplo de <c>MercadoLivreAccountConnected</c> e
/// <c>MercadoLivreAccountDisconnected</c> (SPEC-0020@1), <c>MercadoLivreAccountReauthorizationRequired</c> (SPEC-0021@1)
/// e <c>MercadoLivreItemChanged</c> (SPEC-0023@1), desserializados pelos tipos publicados em
/// <c>MercadoLivre.IntegrationEvents</c> e entregues aos handlers do Listings, são aceitos e processados.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingsEventsContractTests(PostgresFixture postgres)
{
    /// <summary>Schema v1 de MercadoLivreItemChanged (seção 6 da SPEC-0023): campo JSON → tipo .NET.</summary>
    private static readonly Dictionary<string, Type> ItemChangedV1 = new(StringComparer.Ordinal)
    {
        ["accountId"] = typeof(Guid),
        ["workspaceId"] = typeof(Guid),
        ["mlUserId"] = typeof(long),
        ["itemId"] = typeof(string),
        ["notificationId"] = typeof(string),
        ["occurredAt"] = typeof(DateTimeOffset),
    };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("spec", "SPEC-0022:CT-01")]
    public async Task Payloads_v1_dos_eventos_consumidos_sao_desserializados_pelos_tipos_publicados_e_processados_pelos_handlers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // O tipo publicado de MercadoLivreItemChanged é exatamente o schema v1 da SPEC-0023
        MercadoLivreEventsContractTests.AssertPublishedSchema(typeof(MercadoLivreItemChanged), ItemChangedV1);

        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var accountId = Guid.NewGuid();
        const long MlUserId = 3_400_000_001; // maior que int32: o contrato é int64
        var item = ListingItem.New("active", price: 80m);
        gateway.SetSeller(accountId, MlUserId); // anúncios do próprio vendedor (SPEC-0029: alheio é recusado)
        gateway.Put(accountId, item, ListingItem.New("closed"));

        // Dado o payload de MercadoLivreAccountConnected v1; quando desserializado e entregue: a conta é importada
        var connected = Deserialize<MercadoLivreAccountConnected>(
            $$"""
            { "accountId": "{{accountId:D}}", "workspaceId": "{{owner.WorkspaceId:D}}", "mlUserId": {{MlUserId}},
              "siteId": "MLB", "reconnected": false, "occurredAt": "2026-10-30T12:00:00Z" }
            """);
        Assert.Equal(MlUserId, connected.MlUserId);
        await ListingsHost.PublishAsync(factory, connected);
        var imported = await ListingsDatabase.WaitForIdleAsync(connectionString, accountId, after: null, cancellationToken);
        Assert.Equal(2, imported.ItemCount);
        Assert.Equal(owner.WorkspaceId, imported.WorkspaceId);

        // Dado o payload de MercadoLivreItemChanged v1; quando entregue: o anúncio é relido e atualizado
        gateway.Put(accountId, item with { Price = 88m, LastUpdated = item.LastUpdated.AddHours(1) });
        var changed = Deserialize<MercadoLivreItemChanged>(
            $$"""
            { "accountId": "{{accountId:D}}", "workspaceId": "{{owner.WorkspaceId:D}}", "mlUserId": {{MlUserId}},
              "itemId": "{{item.Id}}", "notificationId": "5f3a9c1e2b7d4a0012345678", "occurredAt": "2026-10-30T12:05:00.123Z" }
            """);
        Assert.Equal(item.Id, changed.ItemId);
        await ListingsHost.PublishAsync(factory, changed);
        await Eventually.TrueAsync(
            async () => (await ListingsDatabase.ReadListingsAsync(connectionString, accountId, cancellationToken))
                .Any(row => row.ItemId == item.Id && row.Price == 88m),
            TimeSpan.FromSeconds(30),
            "MercadoLivreItemChanged v1 não atualizou o anúncio",
            cancellationToken);

        // Dado o payload de MercadoLivreAccountReauthorizationRequired v1; quando entregue: a sincronização fica paused
        var reauthorization = Deserialize<MercadoLivreAccountReauthorizationRequired>(
            $$"""
            { "accountId": "{{accountId:D}}", "workspaceId": "{{owner.WorkspaceId:D}}", "mlUserId": {{MlUserId}},
              "occurredAt": "2026-10-30T12:10:00Z" }
            """);
        await ListingsHost.PublishAsync(factory, reauthorization);
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, accountId, state => state.Status == "paused", "MercadoLivreAccountReauthorizationRequired v1 não pausou a conta", cancellationToken);
        Assert.Equal(2, (await ListingsDatabase.ReadListingsAsync(connectionString, accountId, cancellationToken)).Count);

        // Dado o payload de MercadoLivreAccountDisconnected v1; quando entregue: anúncios e estado da conta são apagados
        var disconnected = Deserialize<MercadoLivreAccountDisconnected>(
            $$"""
            { "accountId": "{{accountId:D}}", "workspaceId": "{{owner.WorkspaceId:D}}", "mlUserId": {{MlUserId}},
              "occurredAt": "2026-10-30T12:15:00Z" }
            """);
        await ListingsHost.PublishAsync(factory, disconnected);
        await Eventually.TrueAsync(
            async () => await ListingsDatabase.CountRowsAsync(connectionString, accountId, cancellationToken) == (0L, 0L),
            TimeSpan.FromSeconds(30),
            "MercadoLivreAccountDisconnected v1 não apagou anúncios e estado da conta",
            cancellationToken);
    }

    private static T Deserialize<T>(string json)
    {
        var message = JsonSerializer.Deserialize<T>(json, WebJson);
        Assert.True(message is not null, $"Payload v1 não desserializou em {typeof(T).Name}: {json}");
        Assert.Equal(
            DateTimeOffset.Parse(JsonDocument.Parse(json).RootElement.GetProperty("occurredAt").GetString()!, CultureInfo.InvariantCulture),
            (DateTimeOffset)typeof(T).GetProperty("OccurredAt")!.GetValue(message)!);
        return message;
    }
}
