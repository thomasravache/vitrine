using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-09 — <c>MercadoLivreItemChanged</c>: relê o anúncio (<c>GetItemsAsync([itemId])</c>) e atualiza
/// (<c>Found</c> mais novo), apaga (<c>NotFound</c>) ou mantém (<c>Found</c> mais antigo que o gravado); conta
/// desconhecida é ignorada.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ItemChangedTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-09")]
    public async Task Notificacao_atualiza_com_Found_mais_novo_apaga_com_NotFound_e_mantem_com_Found_mais_antigo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado dois anúncios gravados
        var item = ListingItem.New("active", "Camiseta", 40m);
        var removed = ListingItem.New("active", "Caneca", 20m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, item, removed);

        // Quando chega a notificação e o dublê devolve Found mais novo: o anúncio é atualizado
        var newer = item with { Title = "Camiseta nova", Price = 45m, Status = "paused", SoldQuantity = 9, LastUpdated = item.LastUpdated.AddHours(1) };
        gateway.Put(account.AccountId, newer);
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, item.Id));
        await Eventually.TrueAsync(
            async () => (await Row(connectionString, account, item.Id, cancellationToken))?.Price == 45m,
            TimeSpan.FromSeconds(30),
            "o anúncio não foi atualizado pelo Found mais novo",
            cancellationToken);
        var updated = (await Row(connectionString, account, item.Id, cancellationToken))!;
        Assert.Equal("Camiseta nova", updated.Title);
        Assert.Equal("paused", updated.Status);
        Assert.Equal(9, updated.SoldQuantity);
        Assert.Equal(newer.LastUpdated, updated.MlLastUpdated);
        Assert.Equal(owner.WorkspaceId, updated.WorkspaceId);

        // E com Found mais antigo que o gravado (notificação fora de ordem): o anúncio é mantido
        var readsBefore = gateway.Reads(account.AccountId, item.Id);
        gateway.Put(account.AccountId, item with { Price = 41m, LastUpdated = item.LastUpdated.AddMinutes(30) });
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, item.Id));
        await Eventually.TrueAsync(
            () => gateway.Reads(account.AccountId, item.Id) > readsBefore,
            TimeSpan.FromSeconds(30),
            "a notificação não releu o anúncio no ML",
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var kept = (await Row(connectionString, account, item.Id, cancellationToken))!;
        Assert.Equal(45m, kept.Price);
        Assert.Equal(newer.LastUpdated, kept.MlLastUpdated);

        // E com NotFound: o anúncio é apagado
        gateway.Remove(account.AccountId, removed.Id);
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, removed.Id));
        await Eventually.TrueAsync(
            async () => await Row(connectionString, account, removed.Id, cancellationToken) is null,
            TimeSpan.FromSeconds(30),
            "o anúncio não foi apagado pelo NotFound",
            cancellationToken);
        Assert.NotNull(await Row(connectionString, account, item.Id, cancellationToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-09")]
    public async Task Notificacao_de_conta_desconhecida_e_ignorada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta que o Listings não conhece (nunca recebeu MercadoLivreAccountConnected), com o anúncio no ML
        var item = ListingItem.New();
        var unknown = ListingsHost.NewAccount(gateway, owner.WorkspaceId, item);

        // E uma conta conhecida, para provar que as notificações estão sendo processadas
        var knownItem = ListingItem.New(price: 10m);
        var known = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, knownItem);
        gateway.Put(known.AccountId, knownItem with { Price = 11m, LastUpdated = knownItem.LastUpdated.AddHours(1) });

        // Quando chegam as notificações das duas
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(unknown, item.Id));
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(known, knownItem.Id));
        await Eventually.TrueAsync(
            async () => (await Row(connectionString, known, knownItem.Id, cancellationToken))?.Price == 11m,
            TimeSpan.FromSeconds(30),
            "a notificação da conta conhecida não foi processada",
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        // Então nada acontece para a desconhecida: nenhuma leitura no ML, nenhum anúncio nem estado criado
        Assert.Equal(0, gateway.ReadsOf(unknown.AccountId));
        Assert.Equal((0L, 0L), await ListingsDatabase.CountRowsAsync(connectionString, unknown.AccountId, cancellationToken));
    }

    private static async Task<ListingRow?> Row(string connectionString, ListingsAccount account, string itemId, CancellationToken cancellationToken) =>
        (await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken)).SingleOrDefault(row => row.ItemId == itemId);
}
