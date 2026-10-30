using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.Listings.Application.Sync;

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

    [Fact]
    [Trait("spec", "SPEC-0022:IT-09")]
    public async Task Anuncio_novo_notificado_durante_a_sincronizacao_nao_e_apagado_pela_varredura_dela()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta importada e uma sincronização em curso que já percorreu active e está na busca de paused
        var account = await ListingsHost.ConnectAsync(
            factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New("active"), ListingItem.New("paused"));
        var before = await ListingsDatabase.ReadSyncStateAsync(connectionString, account.AccountId, cancellationToken);
        var pausedSearches = gateway.SearchesStarted(account.AccountId, "paused");
        var created = ListingItem.New("active", "Anúncio criado no meio da sincronização", lastUpdated: ListingItem.BaseTime.AddHours(2));
        using (gateway.HoldSearch("paused"))
        {
            await ListingsHost.PublishAsync(factory, new SyncListings(account.AccountId, SyncTriggers.Reconciliation));
            await Eventually.TrueAsync(
                () => gateway.SearchesStarted(account.AccountId, "paused") > pausedSearches,
                TimeSpan.FromSeconds(30),
                "a sincronização não chegou à busca de paused",
                cancellationToken);

            // Quando um anúncio novo (fora das páginas já percorridas) é notificado e gravado
            gateway.Put(account.AccountId, created);
            await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, created.Id));
            await Eventually.TrueAsync(
                async () => await Row(connectionString, account, created.Id, cancellationToken) is not null,
                TimeSpan.FromSeconds(30),
                "a notificação do anúncio novo não o gravou durante a sincronização",
                cancellationToken);
        }

        // Então, terminada a sincronização, a varredura dela não apaga o anúncio gravado pela notificação
        await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, before!.CompletedAt, cancellationToken);
        var rows = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.True(
            rows.Any(row => row.ItemId == created.Id),
            $"A varredura da sincronização em curso apagou o anúncio gravado pela notificação. Linhas: {string.Join(", ", rows.Select(row => row.ItemId))}");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-09")]
    public async Task Upsert_da_sincronizacao_com_leitura_mais_antiga_nao_sobrescreve_a_notificacao_mais_nova()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var item = ListingItem.New("active", price: 40m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, item);
        var before = await ListingsDatabase.ReadSyncStateAsync(connectionString, account.AccountId, cancellationToken);

        // Dado uma sincronização que leu o anúncio em T1 e ainda não gravou o lote
        var t1 = item with { Price = 41m, LastUpdated = item.LastUpdated.AddHours(1) };
        var t2 = item with { Price = 42m, LastUpdated = item.LastUpdated.AddHours(2) };
        gateway.Put(account.AccountId, t1);
        using (var syncRead = gateway.HoldNextRead())
        {
            await ListingsHost.PublishAsync(factory, new SyncListings(account.AccountId, SyncTriggers.Reconciliation));
            Assert.Contains(item.Id, await syncRead.Reached.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

            // Quando a notificação lê e grava T2 (> T1) antes do lote da sincronização
            gateway.Put(account.AccountId, t2);
            await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, item.Id));
            await Eventually.TrueAsync(
                async () => (await Row(connectionString, account, item.Id, cancellationToken))?.MlLastUpdated == t2.LastUpdated,
                TimeSpan.FromSeconds(30),
                "a notificação não gravou T2 durante a sincronização",
                cancellationToken);
        }

        // Então o lote da sincronização (lido em T1) não sobrescreve T2, e o anúncio continua lá depois da varredura
        await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, before!.CompletedAt, cancellationToken);
        var row = await Row(connectionString, account, item.Id, cancellationToken);
        Assert.True(row is not null, "O anúncio sumiu depois da sincronização.");
        Assert.Equal(t2.LastUpdated, row.MlLastUpdated);
        Assert.Equal(42m, row.Price);
    }

    private static async Task<ListingRow?> Row(string connectionString, ListingsAccount account, string itemId, CancellationToken cancellationToken) =>
        (await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken)).SingleOrDefault(row => row.ItemId == itemId);
}
