using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0028:IT-01 — os handlers do Listings que recebem <c>IMercadoLivreItems</c> (<c>SyncListingsHandler</c> e
/// <c>ItemChangedHandler</c>) rodam com o gateway <b>real</b> do módulo MercadoLivre (implementação interna), sem dublê:
/// o ML é simulado pelo WireMock (<see cref="MercadoLivreFake"/>, SPEC-0021). Os demais testes do Listings trocam o
/// gateway por um dublê público e por isso não viam a falha de geração de código do Wolverine.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RealGatewayTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0028:IT-01")]
    public async Task Sincronizacao_e_notificacao_gravam_e_atualizam_anuncios_com_o_gateway_real_do_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado o host com o gateway real (sem dublê de IMercadoLivreItems) e o ML simulado pelo WireMock
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // E uma conta conectada com dois anúncios no ML
        var seller = new MlSeller(MlItem.SellerId, $"LOJA_TESTE_{MlItem.SellerId}");
        var account = await MercadoLivreGateway.SeedAccountAsync(factory, seller, MlTokens.New(), DateTimeOffset.UtcNow.AddHours(6), cancellationToken);
        var ids = MlItem.NewIds(2);
        fake.SearchResponds(seller.Id, ids);
        fake.BulkResponds();

        // Quando a conexão é publicada e o SyncListings da importação é processado
        await ListingsHost.PublishAsync(
            factory,
            new MercadoLivreAccountConnected(account.AccountId, account.WorkspaceId, seller.Id, "MLB", Reconnected: false, DateTimeOffset.UtcNow));
        await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, after: null, cancellationToken);

        // Então os dois anúncios são gravados em listings.listings
        var imported = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.Equal(ids.Order(StringComparer.Ordinal), imported.Select(row => row.ItemId).Order(StringComparer.Ordinal));
        Assert.All(imported, row => Assert.Equal(account.WorkspaceId, row.WorkspaceId));

        // Quando o ML passa a devolver o primeiro anúncio alterado (mais novo) e chega MercadoLivreItemChanged dele
        const string NewTitle = "Produto alterado no ML";
        fake.Reset();
        fake.BulkResponds(id => MlItem.Found(id, body =>
        {
            body["title"] = NewTitle;
            body["price"] = 99.9m;
            body["last_updated"] = "2026-10-30T18:00:00.000Z";
        }));
        await ListingsHost.PublishAsync(
            factory,
            new MercadoLivreItemChanged(account.AccountId, account.WorkspaceId, seller.Id, ids[0], Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));

        // Então o anúncio é atualizado com a releitura do gateway real; o outro fica como estava
        await Eventually.TrueAsync(
            async () => (await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken))
                .Any(row => row.ItemId == ids[0] && row.Title == NewTitle),
            TimeSpan.FromSeconds(30),
            "o anúncio não foi atualizado pelo MercadoLivreItemChanged com o gateway real",
            cancellationToken);
        var rows = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        var updated = Assert.Single(rows, row => row.ItemId == ids[0]);
        Assert.Equal(99.9m, updated.Price);
        Assert.NotEqual(NewTitle, Assert.Single(rows, row => row.ItemId == ids[1]).Title);
    }
}
