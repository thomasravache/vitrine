using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-03 — <c>GET /api/v1/listings</c>: filtros de status, conta e busca (trecho do título ou código
/// <c>MLB…</c>), paginação por <c>nextCursor</c> estável e ordenada (<c>lastUpdated desc, itemId</c>) e 400
/// <c>VALIDATION_FAILED</c> para parâmetros inválidos.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingsQueryTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-03")]
    public async Task Filtros_busca_e_paginacao_por_cursor_devolvem_cada_item_uma_vez_na_ordem_do_contrato()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble { PageSize = 7 };
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado 45 anúncios em 2 contas (A: 20 active, 6 paused, 4 closed; B: 10 active, 3 paused, 2 closed), com empates de
        // lastUpdated e 9 títulos com "Camiseta" em caixas diferentes
        var accountAItems = Build(20, 6, 4, offsetMinutes: 0);
        var accountBItems = Build(10, 3, 2, offsetMinutes: 1000);
        var accountA = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, accountAItems);
        var accountB = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, accountBItems);
        var all = accountAItems.Select(item => (Item: item, Account: accountA.AccountId))
            .Concat(accountBItems.Select(item => (Item: item, Account: accountB.AccountId)))
            .ToList();

        // Quando status=all é paginado com limit=20 pelo nextCursor até o fim
        var (everything, pages) = await api.ListAllAsync("status=all&limit=20", cancellationToken);

        // Então cada um dos 45 aparece exatamente uma vez, na ordem lastUpdated desc, itemId, em 3 páginas
        Assert.Equal(3, pages);
        AssertExactlyInContractOrder(all.Select(entry => entry.Item), everything);
        Assert.All(everything, ListingsApi.AssertItemShape);
        foreach (var listed in everything)
        {
            var source = all.Single(entry => entry.Item.Id == ListingsApi.ItemId(listed));
            Assert.Equal(source.Account, listed.GetProperty("accountId").GetGuid());
            Assert.Equal(source.Item.Title, listed.GetProperty("title").GetString());
            Assert.Equal(source.Item.Price, listed.GetProperty("price").GetDecimal());
            Assert.Equal(source.Item.Status, listed.GetProperty("status").GetString());
            Assert.Equal(source.Item.Permalink!.ToString(), listed.GetProperty("permalink").GetString());
            Assert.Equal(source.Item.ThumbnailUrl!.ToString(), listed.GetProperty("thumbnailUrl").GetString());
            Assert.Equal(source.Item.LastUpdated, ListingsApi.LastUpdated(listed));
        }

        // E sem status (padrão active), com o limite padrão de 20: só os 30 ativos
        var (active, activePages) = await api.ListAllAsync(string.Empty, cancellationToken);
        Assert.Equal(2, activePages);
        AssertExactlyInContractOrder(all.Where(entry => entry.Item.Status == "active").Select(entry => entry.Item), active);

        // E status=paused&accountId=A: só os 6 pausados de A
        var (pausedA, _) = await api.ListAllAsync($"status=paused&accountId={accountA.AccountId:D}&limit=4", cancellationToken);
        AssertExactlyInContractOrder(accountAItems.Where(item => item.Status == "paused"), pausedA);

        // E q por trecho do título, sem diferenciar maiúsculas: os 9 com "camiseta" (em qualquer status, com status=all)
        var (byText, _) = await api.ListAllAsync("status=all&q=cAmIsEtA&limit=5", cancellationToken);
        AssertExactlyInContractOrder(
            all.Where(entry => entry.Item.Title.Contains("camiseta", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Item),
            byText);
        Assert.Equal(9, byText.Count);

        // E q por código exato MLB…: só aquele anúncio (mesmo pausado, com status=all)
        var target = accountBItems.First(item => item.Status == "paused");
        var (byCode, _) = await api.ListAllAsync($"status=all&q={target.Id}", cancellationToken);
        Assert.Equal([target.Id], byCode.Select(ListingsApi.ItemId));
    }

    [Theory]
    [Trait("spec", "SPEC-0022:IT-03")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=51", "limit")]
    [InlineData("status=xyz", "status")]
    [InlineData("cursor=nao-e-um-cursor", "cursor")]
    [InlineData("cursor=TRUNCATE", "cursor")]
    [InlineData("q=LONG", "q")]
    [InlineData("limit=abc", "limit")]
    [InlineData("accountId=xyz", "accountId")]
    public async Task Parametro_invalido_responde_400_VALIDATION_FAILED_com_o_campo_em_errors(string query, string field)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta com anúncios suficientes para haver um nextCursor de verdade
        await ListingsHost.ConnectAsync(
            factory, gateway, owner.WorkspaceId, cancellationToken, [.. Enumerable.Range(1, 3).Select(n => ListingItem.New(lastUpdated: ListingItem.BaseTime.AddMinutes(n)))]);
        var first = await api.ListSuccessfullyAsync("limit=1", cancellationToken);
        Assert.NotNull(first.NextCursor);
        var resolved = query switch
        {
            "cursor=TRUNCATE" => $"cursor={Uri.EscapeDataString(first.NextCursor[..(first.NextCursor.Length / 2)])}",
            "q=LONG" => $"q={new string('a', 101)}",
            _ => query,
        };

        // Quando GET /listings é chamado com o parâmetro inválido
        using var response = await api.ListAsync(resolved, cancellationToken);

        // Então 400 VALIDATION_FAILED com o campo em errors
        var problem = await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED", response, cancellationToken);
        Assert.True(
            problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.TryGetProperty(field, out _),
            $"VALIDATION_FAILED sem errors.{field}: {problem}");
    }

    /// <summary>
    /// Anúncios da conta com lastUpdated em grupos de 3 iguais (empates resolvidos por itemId) e "Camiseta" no título de
    /// 1 em cada 5, em caixas diferentes.
    /// </summary>
    private static MercadoLivreItem[] Build(int active, int paused, int closed, int offsetMinutes)
    {
        var statuses = Enumerable.Repeat("active", active).Concat(Enumerable.Repeat("paused", paused)).Concat(Enumerable.Repeat("closed", closed));
        return [.. statuses.Select((status, index) => ListingItem.New(
            status,
            title: index % 5 == 0 ? (index % 2 == 0 ? $"Camiseta básica {index}" : $"CAMISETA estampada {index}") : $"Produto variado {index}",
            price: 10m + index,
            lastUpdated: ListingItem.BaseTime.AddMinutes(offsetMinutes + (index / 3))))];
    }

    private static void AssertExactlyInContractOrder(IEnumerable<MercadoLivreItem> expected, List<JsonElement> listed)
    {
        var expectedOrder = expected
            .OrderByDescending(item => item.LastUpdated)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToList();
        var listedIds = listed.Select(ListingsApi.ItemId).ToList();
        Assert.Equal(listedIds.Count, listedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expectedOrder, listedIds);
    }
}
