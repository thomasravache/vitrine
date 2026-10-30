using System.Globalization;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>Busca de IDs (offset e <c>scan</c>) e leitura em lote por <c>/items/bulk</c> (SPEC-0021).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewayItemsTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0021:IT-06")]
    public async Task Busca_com_total_250_pagina_por_offset_em_3_paginas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com 250 anúncios ativos
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var ids = MlItem.NewIds(250);
        fake.SearchResponds(account.Seller.Id, ids, requiredToken: account.Tokens.AccessToken);

        // Quando SearchItemIdsAsync é chamado
        var pages = await MercadoLivreGateway.SearchItemIdsAsync(factory, account.AccountId, "active", cancellationToken);

        // Então há 3 páginas por offset (0, 100, 200), com status=active e limit=100, sem scan
        var calls = fake.SearchCalls(account.Seller.Id).Select(MercadoLivreFake.QueryOf).ToList();
        Assert.Equal(["0", "100", "200"], calls.Select(query => query.GetValueOrDefault("offset")));
        Assert.All(calls, query =>
        {
            Assert.Equal("active", query.GetValueOrDefault("status"));
            Assert.Equal("100", query.GetValueOrDefault("limit"));
            Assert.False(query.ContainsKey("search_type"), "Com total ≤ 1000 a busca não usa scan.");
        });

        // E todos os IDs são devolvidos
        Assert.Equal(ids.Order(StringComparer.Ordinal), pages.SelectMany(page => page).Distinct().Order(StringComparer.Ordinal));
        Assert.All(pages, page => Assert.InRange(page.Count, 1, 100));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-06")]
    public async Task Busca_com_total_2300_passa_a_scan_e_segue_o_scroll_id_ate_results_vazio()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com 2300 anúncios pausados (offset ≥ 1000 é recusado pelo ML)
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var ids = MlItem.NewIds(2300);
        fake.SearchResponds(account.Seller.Id, ids, requiredToken: account.Tokens.AccessToken);

        // Quando SearchItemIdsAsync é chamado
        var pages = await MercadoLivreGateway.SearchItemIdsAsync(factory, account.AccountId, "paused", cancellationToken);

        // Então todos os IDs são devolvidos
        Assert.Equal(ids.Order(StringComparer.Ordinal), pages.SelectMany(page => page).Distinct().Order(StringComparer.Ordinal));

        // E, depois da 1ª página por offset, as páginas são scan com scroll_id encadeado até results vazio
        var calls = fake.SearchCalls(account.Seller.Id).Select(MercadoLivreFake.QueryOf).ToList();
        Assert.Equal("0", calls[0].GetValueOrDefault("offset") ?? "0");
        Assert.False(calls[0].ContainsKey("search_type"), "A 1ª página é por offset (é ela que revela total > 1000).");
        var scans = calls.Skip(1).ToList();
        Assert.All(scans, query =>
        {
            Assert.Equal("scan", query.GetValueOrDefault("search_type"));
            Assert.Equal("paused", query.GetValueOrDefault("status"));
            Assert.Equal("100", query.GetValueOrDefault("limit"));
        });
        Assert.False(scans[0].ContainsKey("scroll_id"), "A 1ª página scan não leva scroll_id.");
        for (var index = 1; index < scans.Count; index++)
        {
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"scroll-{index}"), scans[index].GetValueOrDefault("scroll_id"));
        }

        // 23 páginas cheias + a vazia que encerra
        Assert.Equal(24, scans.Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-07")]
    public async Task Quarenta_e_cinco_IDs_viram_3_chamadas_bulk_mapeadas_na_ordem_pedida()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado 45 IDs, um deles inexistente (status_code 404), e miniaturas em vários formatos
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var ids = MlItem.NewIds(45);
        var missing = ids[17];
        fake.BulkResponds(id => id == missing ? MlItem.Failed(id, 404) : MlItem.Found(id, Thumbnail(ids.IndexOf(id))), account.Tokens.AccessToken);

        // Quando GetItemsAsync é chamado
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, ids, cancellationToken);

        // Então há 3 chamadas (20 + 20 + 5), com os IDs pedidos e os attributes do contrato
        var calls = fake.BulkCalls;
        Assert.Equal(3, calls.Count);
        var batches = calls.Select(MercadoLivreFake.BulkIdsOf).ToList();
        Assert.Equal([5, 20, 20], batches.Select(batch => batch.Length).Order());
        Assert.Equal(ids.Order(StringComparer.Ordinal), batches.SelectMany(batch => batch).Order(StringComparer.Ordinal));
        Assert.All(calls, call => Assert.Equal(
            MercadoLivreFake.BulkAttributes.Order(StringComparer.Ordinal),
            (MercadoLivreFake.QueryOf(call).GetValueOrDefault("attributes") ?? string.Empty).Split(',').Order(StringComparer.Ordinal)));

        // E devolve um resultado por ID, na ordem pedida: 44 Found mapeados e 1 NotFound
        Assert.Equal(ids, results.Select(result => result.ItemId));
        Assert.Equal(missing, Assert.IsType<MercadoLivreItemResult.NotFound>(results[17]).ItemId);
        for (var index = 0; index < ids.Count; index++)
        {
            if (index == 17)
            {
                continue;
            }

            var item = Assert.IsType<MercadoLivreItemResult.Found>(results[index]).Item;
            Assert.Equal(ids[index], item.Id);
            Assert.Equal(MlItem.SellerId, item.SellerId);
            Assert.Equal($"Produto de teste {ids[index]}", item.Title);
            Assert.Equal(1234.56m, item.Price);
            Assert.Equal("BRL", item.CurrencyId);
            Assert.Equal(7, item.AvailableQuantity);
            Assert.Equal(42, item.SoldQuantity);
            Assert.Equal("active", item.Status);
            Assert.Equal(new Uri($"https://produto.mercadolivre.com.br/{ids[index]}-produto-de-teste"), item.Permalink);
            Assert.Equal("gold_special", item.ListingTypeId);
            Assert.Equal(new DateTimeOffset(2026, 10, 29, 14, 3, 12, TimeSpan.Zero), item.LastUpdated);
            Assert.Equal(ExpectedThumbnail(index, ids[index]), item.ThumbnailUrl);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-07")]
    public async Task Item_com_outro_status_code_vira_Unavailable_sem_lancar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado um lote com um item sem acesso (status_code 403)
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var ids = MlItem.NewIds(3);
        fake.BulkResponds(id => id == ids[1] ? MlItem.Failed(id, 403) : MlItem.Found(id), account.Tokens.AccessToken);

        // Quando GetItemsAsync é chamado
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, ids, cancellationToken);

        // Então devolve Found, Unavailable e Found, na ordem pedida
        Assert.Equal(ids, results.Select(result => result.ItemId));
        Assert.IsType<MercadoLivreItemResult.Found>(results[0]);
        Assert.Equal(ids[1], Assert.IsType<MercadoLivreItemResult.Unavailable>(results[1]).ItemId);
        Assert.IsType<MercadoLivreItemResult.Found>(results[2]);
    }

    // Variações da miniatura por posição: secure_thumbnail https em *.mlstatic.com; só thumbnail http (vira https);
    // host fora do mlstatic.com; nenhuma; e um host que só imita o mlstatic.com.
    private static Action<Dictionary<string, object?>> Thumbnail(int index) => body =>
    {
        var id = (string)body["id"]!;
        switch (index % 5)
        {
            case 1:
                body.Remove("secure_thumbnail");
                break;
            case 2:
                body["secure_thumbnail"] = $"https://imagens.example.com/{id}.jpg";
                body["thumbnail"] = $"http://imagens.example.com/{id}.jpg";
                break;
            case 3:
                body.Remove("secure_thumbnail");
                body.Remove("thumbnail");
                break;
            case 4:
                body["secure_thumbnail"] = $"https://mlstatic.com.example.net/{id}.jpg";
                body["thumbnail"] = $"http://mlstatic.com.example.net/{id}.jpg";
                break;
        }
    };

    private static Uri? ExpectedThumbnail(int index, string id) => (index % 5) switch
    {
        0 or 1 => new Uri($"https://http2.mlstatic.com/D_{id}-I.jpg"),
        _ => null,
    };
}
