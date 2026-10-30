using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.Listings.Application.Sync;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-01 e IT-02 — importação ao conectar (<c>MercadoLivreAccountConnected</c>) e reimportação com marcar e
/// varrer, com Postgres e Wolverine reais e o gateway trocado pelo dublê do contrato SPEC-0021@1.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ImportTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-01")]
    public async Task Conta_conectada_tem_os_anuncios_dos_3_status_gravados_e_sync_states_idle_com_item_count()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado o dublê com 3 anúncios ativos, 1 pausado e 1 encerrado
        var items = new[]
        {
            ListingItem.New("active", "Camiseta Azul", 59.90m, ListingItem.BaseTime.AddMinutes(1)),
            ListingItem.New("active", "Caneca Branca", 25.00m, ListingItem.BaseTime.AddMinutes(2)),
            ListingItem.New("active", "Boné Preto", 39.99m, ListingItem.BaseTime.AddMinutes(3)),
            ListingItem.New("paused", "Mochila", 149.00m, ListingItem.BaseTime.AddMinutes(4)),
            ListingItem.New("closed", "Garrafa", 19.50m, ListingItem.BaseTime.AddMinutes(5)),
        };
        var account = ListingsHost.NewAccount(gateway, owner.WorkspaceId, items);

        // Quando MercadoLivreAccountConnected é publicado
        await ListingsHost.PublishAsync(factory, ListingsHost.Connected(account));

        // Então sync_states fica idle com item_count=5, no workspace do evento
        var state = await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, after: null, cancellationToken);
        Assert.Equal(5, state.ItemCount);
        Assert.Equal(owner.WorkspaceId, state.WorkspaceId);
        Assert.Null(state.LastError);

        // E os 5 anúncios ficam gravados com o workspace do evento e os dados do ML
        var rows = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.Equal(items.Select(item => item.Id).Order(StringComparer.Ordinal), rows.Select(row => row.ItemId));
        foreach (var item in items)
        {
            var row = Assert.Single(rows, row => row.ItemId == item.Id);
            Assert.Equal(owner.WorkspaceId, row.WorkspaceId);
            Assert.Equal(item.Title, row.Title);
            Assert.Equal(item.Price, row.Price);
            Assert.Equal(item.CurrencyId, row.CurrencyId);
            Assert.Equal(item.AvailableQuantity, row.AvailableQuantity);
            Assert.Equal(item.SoldQuantity, row.SoldQuantity);
            Assert.Equal(item.Status, row.Status);
            Assert.Equal(item.Permalink!.ToString(), row.Permalink);
            Assert.Equal(item.ThumbnailUrl!.ToString(), row.ThumbnailUrl);
            Assert.Equal(item.LastUpdated, row.MlLastUpdated);
        }

        // E todos foram gravados pela mesma execução
        Assert.Single(rows.Select(row => row.SyncRunId).Distinct());
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-02")]
    public async Task Reimportacao_remove_o_anuncio_omitido_atualiza_o_alterado_e_nao_duplica()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta importada com 6 anúncios (páginas do dublê repetem IDs entre si)
        var items = Enumerable.Range(1, 4).Select(n => ListingItem.New("active", lastUpdated: ListingItem.BaseTime.AddMinutes(n)))
            .Append(ListingItem.New("paused"))
            .Append(ListingItem.New("closed"))
            .ToArray();
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, items);
        var before = await ListingsDatabase.ReadSyncStateAsync(connectionString, account.AccountId, cancellationToken);
        Assert.Equal(6, (await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken)).Count);

        // Quando o dublê passa a omitir 1 anúncio e a alterar o preço de outro, e a sincronização roda de novo
        var omitted = items[0];
        var changed = items[1] with { Price = 249.90m, LastUpdated = items[1].LastUpdated.AddHours(1) };
        gateway.Remove(account.AccountId, omitted.Id);
        gateway.Put(account.AccountId, changed);
        await ListingsHost.PublishAsync(factory, new SyncListings(account.AccountId, SyncTriggers.Reconciliation));

        // Então o omitido some, o preço é atualizado e o total de linhas confere, sem duplicatas
        var state = await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, before!.CompletedAt, cancellationToken);
        var rows = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.Equal(5, rows.Count);
        Assert.Equal(5, state.ItemCount);
        Assert.Equal(rows.Count, rows.Select(row => row.ItemId).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(rows, row => row.ItemId == omitted.Id);
        var updated = Assert.Single(rows, row => row.ItemId == changed.Id);
        Assert.Equal(249.90m, updated.Price);
        Assert.Equal(changed.LastUpdated, updated.MlLastUpdated);
        Assert.Equal(items.Skip(1).Select(item => item.Id).Order(StringComparer.Ordinal), rows.Select(row => row.ItemId));
    }
}
