using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.Listings.Infrastructure;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0029:IT-01 — notificação forjada: o ML não assina as notificações, então um <c>MercadoLivreItemChanged</c>
/// pode apontar um anúncio de <b>outro</b> vendedor para a conta. A releitura (o <c>/items/bulk</c> devolve itens
/// públicos de qualquer vendedor) traz <c>Found</c> com <c>SellerId</c> alheio: nada é gravado, a cópia local com esse
/// <c>itemId</c> é apagada e <c>vitrine.listings.item_changes{result=foreign_item}</c> conta; anúncios do próprio
/// vendedor seguem atualizando (padrão do SPEC-0022:IT-09).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ForeignItemTests(PostgresFixture postgres)
{
    private const string Metric = "vitrine.listings.item_changes";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    [Fact]
    [Trait("spec", "SPEC-0029:IT-01")]
    public async Task Anuncio_de_outro_vendedor_nao_entra_no_catalogo_e_o_do_proprio_vendedor_segue_atualizando()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var metrics = new AuthMetricsListener(factory.Services.GetRequiredService<IMeterFactory>(), ListingsModule.MeterName);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta conectada com seus anúncios
        var own = ListingItem.New("active", "Camiseta", 40m);
        var other = ListingItem.New("active", "Caneca", 20m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, own, other);
        var before = Snapshot(await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken));
        Assert.Equal(2, before.Count);

        // E o gateway devolvendo Found para um anúncio cujo SellerId é de outro vendedor
        var foreign = ListingItem.New("active", "Anúncio de outro vendedor", 1m, ListingItem.BaseTime.AddHours(3)) with
        {
            SellerId = account.MlUserId + 1,
        };
        gateway.Put(account.AccountId, foreign);
        Assert.Equal(account.MlUserId + 1, gateway.Get(account.AccountId, foreign.Id).SellerId);

        // Quando chega MercadoLivreItemChanged desse anúncio para a conta
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, foreign.Id));
        await Eventually.TrueAsync(
            () => gateway.Reads(account.AccountId, foreign.Id) > 0,
            TimeSpan.FromSeconds(30),
            "a notificação não releu o anúncio no ML",
            cancellationToken);
        await Task.Delay(Settle, cancellationToken);

        // Então nenhuma linha é gravada para ele e os anúncios da conta não mudam
        var afterForeign = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.DoesNotContain(afterForeign, row => row.ItemId == foreign.Id);
        Assert.Equal(before, Snapshot(afterForeign));

        // E a métrica foreign_item é incrementada
        await Eventually.TrueAsync(
            () => metrics.Total(Metric, "foreign_item") == 1,
            TimeSpan.FromSeconds(10),
            $"{Metric}{{result=foreign_item}} deveria ser 1. {metrics.Describe()}",
            cancellationToken);

        // Dado uma cópia local já existente com um itemId de outro vendedor (gravada antes da correção)
        var planted = ListingItem.New("active", "Anúncio alheio já gravado", 2m, ListingItem.BaseTime.AddHours(3)) with
        {
            SellerId = account.MlUserId + 2,
        };
        await InsertListingAsync(connectionString, account, planted.Id, planted.Title, cancellationToken);
        gateway.Put(account.AccountId, planted);

        // Quando chega a notificação dele; então a cópia é apagada, sem tocar nos anúncios da conta
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, planted.Id));
        await Eventually.TrueAsync(
            async () => !(await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken))
                .Any(row => row.ItemId == planted.Id),
            TimeSpan.FromSeconds(30),
            "a cópia local do anúncio de outro vendedor não foi apagada",
            cancellationToken);
        Assert.Equal(before, Snapshot(await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken)));
        await Eventually.TrueAsync(
            () => metrics.Total(Metric, "foreign_item") == 2,
            TimeSpan.FromSeconds(10),
            $"{Metric}{{result=foreign_item}} deveria ser 2. {metrics.Describe()}",
            cancellationToken);

        // E uma notificação de anúncio do próprio vendedor continua atualizando
        gateway.Put(account.AccountId, own with { Title = "Camiseta nova", Price = 45m, LastUpdated = own.LastUpdated.AddHours(1) });
        Assert.Equal(account.MlUserId, gateway.Get(account.AccountId, own.Id).SellerId);
        await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, own.Id));
        await Eventually.TrueAsync(
            async () => (await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken))
                .Any(row => row.ItemId == own.Id && row.Price == 45m && row.Title == "Camiseta nova"),
            TimeSpan.FromSeconds(30),
            "o anúncio do próprio vendedor não foi atualizado pela notificação",
            cancellationToken);
        Assert.True(metrics.Total(Metric, "foreign_item") == 2, metrics.Describe());
    }

    private static List<(string ItemId, string Title, decimal Price, DateTimeOffset MlLastUpdated)> Snapshot(IEnumerable<ListingRow> rows) =>
        [.. rows.Select(row => (row.ItemId, row.Title, row.Price, row.MlLastUpdated)).OrderBy(row => row.ItemId, StringComparer.Ordinal)];

    private static async Task InsertListingAsync(
        string connectionString, ListingsAccount account, string itemId, string title, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into listings.listings (account_id, item_id, workspace_id, title, price, currency_id, available_quantity,
                                           sold_quantity, status, permalink, thumbnail_url, ml_last_updated, sync_run_id, updated_at)
            values (@account, @item, @workspace, @title, 2, 'BRL', 1, 0, 'active', null, null, @updated, null, now())
            """,
            connection);
        command.Parameters.AddWithValue("account", account.AccountId);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("workspace", account.WorkspaceId);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("updated", ListingItem.BaseTime);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }
}
