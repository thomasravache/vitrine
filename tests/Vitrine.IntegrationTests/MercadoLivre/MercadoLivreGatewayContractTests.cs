using System.Globalization;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Contratos públicos da SPEC-0021 consumidos pela SPEC-0022: o evento <c>MercadoLivreAccountReauthorizationRequired</c>
/// v1 e o mapeamento do <c>/items/bulk</c> para <see cref="MercadoLivreItem"/>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MercadoLivreGatewayContractTests(PostgresFixture postgres)
{
    /// <summary>Schema v1 de MercadoLivreAccountReauthorizationRequired (seção 6 da SPEC-0021): campo JSON → tipo .NET.</summary>
    private static readonly Dictionary<string, Type> ReauthorizationRequiredV1 = new(StringComparer.Ordinal)
    {
        ["accountId"] = typeof(Guid),
        ["workspaceId"] = typeof(Guid),
        ["mlUserId"] = typeof(long),
        ["occurredAt"] = typeof(DateTimeOffset),
    };

    [Fact]
    [Trait("spec", "SPEC-0021:CT-01")]
    public async Task Assinante_recebe_ReauthorizationRequired_v1_com_os_campos_e_tipos_do_contrato()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // O tipo publicado em MercadoLivre.IntegrationEvents é exatamente o schema v1
        MercadoLivreEventsContractTests.AssertPublishedSchema(typeof(MercadoLivreAccountReauthorizationRequired), ReauthorizationRequiredV1);

        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma renovação recusada (invalid_grant) de uma conta com token expirando
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken);
        fake.TokenFails(400, "invalid_grant");
        var before = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        var after = DateTimeOffset.UtcNow;

        // Quando o assinante de teste recebe o evento
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountReauthorizationRequired>(account.AccountId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreAccountReauthorizationRequired da conta",
            cancellationToken);

        // Então o payload valida contra o schema v1 (mlUserId int64, occurredAt UTC), com os valores da conta
        var payload = MercadoLivreEventsContractTests.AssertPayload(
            Assert.Single(MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountReauthorizationRequired>(account.AccountId)),
            ReauthorizationRequiredV1);
        Assert.Equal(account.AccountId, payload.GetProperty("accountId").GetGuid());
        Assert.Equal(account.WorkspaceId, payload.GetProperty("workspaceId").GetGuid());
        Assert.Equal(account.Seller.Id, payload.GetProperty("mlUserId").GetInt64());
        Assert.InRange(MercadoLivreEventsContractTests.OccurredAt(payload), before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:CT-02")]
    public async Task Resposta_documentada_do_items_bulk_vira_MercadoLivreItem_com_todos_os_campos_do_contrato()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a fixture da resposta do /items/bulk (formato documentado, versionada com URL e data)
        var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "MercadoLivre", "Fixtures", "items-bulk.json"), cancellationToken));
        var entries = fixture.RootElement.GetProperty("response").EnumerateArray().ToList();
        var ids = entries.Select(entry => entry.GetProperty("id").GetString()!).ToList();

        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.BulkRespondsRaw(fixture.RootElement.GetProperty("response").GetRawText());

        // Quando mapeada pelo gateway
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, ids, cancellationToken);

        // Então cada item 200 vira MercadoLivreItem com todos os campos do contrato, e o 404 vira NotFound
        Assert.Equal(ids, results.Select(result => result.ItemId));
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.GetProperty("status_code").GetInt32() == 404)
            {
                Assert.IsType<MercadoLivreItemResult.NotFound>(results[index]);
                continue;
            }

            var body = entry.GetProperty("body");
            var item = Assert.IsType<MercadoLivreItemResult.Found>(results[index]).Item;
            Assert.Equal(body.GetProperty("id").GetString(), item.Id);
            Assert.Equal(body.GetProperty("seller_id").GetInt64(), item.SellerId);
            Assert.Equal(body.GetProperty("title").GetString(), item.Title);
            Assert.Equal(body.GetProperty("price").GetDecimal(), item.Price);
            Assert.Equal(body.GetProperty("currency_id").GetString(), item.CurrencyId);
            Assert.Equal(body.GetProperty("available_quantity").GetInt32(), item.AvailableQuantity);
            Assert.Equal(body.GetProperty("sold_quantity").GetInt32(), item.SoldQuantity);
            Assert.Equal(body.GetProperty("status").GetString(), item.Status);
            Assert.Equal(new Uri(body.GetProperty("permalink").GetString()!), item.Permalink);
            Assert.Equal(body.GetProperty("listing_type_id").GetString(), item.ListingTypeId);
            Assert.Equal(
                DateTimeOffset.Parse(body.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture),
                item.LastUpdated);

            // ThumbnailUrl = secure_thumbnail; sem ele, thumbnail com o esquema trocado para https
            var expectedThumbnail = body.TryGetProperty("secure_thumbnail", out var secure)
                ? secure.GetString()!
                : "https://" + body.GetProperty("thumbnail").GetString()!["http://".Length..];
            Assert.Equal(new Uri(expectedThumbnail), item.ThumbnailUrl);
        }

        Assert.Contains(results, result => result is MercadoLivreItemResult.Found);
        Assert.Contains(results, result => result is MercadoLivreItemResult.NotFound);
    }
}
