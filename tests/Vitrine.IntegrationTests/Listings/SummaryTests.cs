using System.Text.Json;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>SPEC-0022:IT-04 — <c>GET /api/v1/listings/summary</c>: totais por status e estado de sincronização por conta.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SummaryTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-04")]
    public async Task Resumo_tem_os_totais_por_status_e_o_estado_de_cada_conta_idle_failed_e_paused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado a conta A idle (2 active, 1 paused, 1 closed e 1 que passou a under_review por notificação)
        var underReview = ListingItem.New("active");
        var idle = await ListingsHost.ConnectAsync(
            factory, gateway, owner.WorkspaceId, cancellationToken,
            ListingItem.New("active"), ListingItem.New("active"), ListingItem.New("paused"), ListingItem.New("closed"), underReview);
        gateway.Put(idle.AccountId, underReview with { Status = "under_review", LastUpdated = underReview.LastUpdated.AddHours(1) });
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(idle, underReview.Id));
        await Eventually.TrueAsync(
            async () => (await ListingsDatabase.ReadListingsAsync(connectionString, idle.AccountId, cancellationToken))
                .Any(row => row.ItemId == underReview.Id && row.Status == "under_review"),
            TimeSpan.FromSeconds(30),
            "o anúncio não passou a under_review depois de MercadoLivreItemChanged",
            cancellationToken);

        // E a conta P paused por reautorização, com 1 anúncio active mantido
        var paused = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New("active"));
        await ListingsHost.PublishAsync(factory, ListingsHost.ReauthorizationRequired(paused));
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, paused.AccountId, state => state.Status == "paused", "a conta não ficou paused", cancellationToken);

        // E a conta F failed: o ML indisponível já na importação
        var failed = ListingsHost.NewAccount(gateway, owner.WorkspaceId, ListingItem.New("active"));
        gateway.FailSearch(failed.AccountId, "active", new MercadoLivreUnavailableException(failed.AccountId, "retries esgotados"));
        await ListingsHost.PublishAsync(factory, ListingsHost.Connected(failed));
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, failed.AccountId, state => state.Status == "failed", "a importação da conta não ficou failed", cancellationToken);

        // Quando GET /listings/summary é chamado
        var summary = await api.SummarySuccessfullyAsync(cancellationToken);

        // Então os totais por status conferem (A: 2 active + 1 paused + 1 closed + 1 other; P: 1 active)
        Assert.Equal(6, summary.GetProperty("total").GetInt32());
        var byStatus = summary.GetProperty("byStatus");
        Assert.Equal(3, byStatus.GetProperty("active").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("paused").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("closed").GetInt32());
        Assert.Equal(1, byStatus.GetProperty("other").GetInt32());

        // E o estado por conta confere
        var accounts = summary.GetProperty("accounts").EnumerateArray().ToDictionary(account => account.GetProperty("accountId").GetGuid());
        Assert.Equal(3, accounts.Count);

        var a = accounts[idle.AccountId];
        Assert.Equal("idle", a.GetProperty("syncStatus").GetString());
        Assert.Equal(5, a.GetProperty("itemCount").GetInt32());
        Assert.True(a.GetProperty("lastSyncedAt").TryGetDateTimeOffset(out _), $"lastSyncedAt da conta idle deveria ser data-hora: {a}");
        Assert.Equal(JsonValueKind.Null, a.GetProperty("lastError").ValueKind);

        var p = accounts[paused.AccountId];
        Assert.Equal("paused", p.GetProperty("syncStatus").GetString());
        Assert.Equal(1, p.GetProperty("itemCount").GetInt32());
        Assert.True(p.GetProperty("lastSyncedAt").TryGetDateTimeOffset(out _), $"lastSyncedAt da conta paused (importada antes) deveria ser data-hora: {p}");

        var f = accounts[failed.AccountId];
        Assert.Equal("failed", f.GetProperty("syncStatus").GetString());
        Assert.Equal(0, f.GetProperty("itemCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("lastSyncedAt").ValueKind);
        Assert.Equal("ML_UNAVAILABLE", f.GetProperty("lastError").GetString());
    }
}
