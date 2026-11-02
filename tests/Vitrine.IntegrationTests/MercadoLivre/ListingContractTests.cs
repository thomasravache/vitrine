using System.Globalization;
using System.Text.Json.Nodes;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Contrato com as fixtures sintéticas da SPEC-0033 em <c>tests/Fixtures/MercadoLivre</c> (compartilhadas com o simulador
/// da SPEC-0034 e os dublês do Listings): as respostas viram os tipos do §6 com todos os campos, e o editor monta os
/// corpos de escrita iguais às fixtures de requisição.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingContractTests(PostgresFixture postgres)
{
    private static readonly string[] SourceFields = ["endpoint", "format", "documentation", "consulted_at", "note"];

    [Theory]
    [Trait("spec", "SPEC-0033:CT-01")]
    [InlineData("item-details.json")]
    [InlineData("item-description.json")]
    [InlineData("category.json")]
    [InlineData("category-attributes.json")]
    [InlineData("item-update-request.json")]
    [InlineData("item-description-request.json")]
    [InlineData("error-title-not-modifiable.json")]
    [InlineData("error-description-invalid.json")]
    [InlineData("error-item-not-active.json")]
    [InlineData("error-conflict.json")]
    public void Fixture_tem_a_origem_anotada(string name)
    {
        // Dado a fixture sintética; então a origem está anotada (endpoint, formato, documentação, data e nota)
        var source = ListingFixtures.Load(name)["_source"]?.AsObject();
        Assert.NotNull(source);
        foreach (var field in SourceFields)
        {
            Assert.False(string.IsNullOrWhiteSpace(source[field]?.GetValue<string>()), $"{name}: _source.{field} ausente.");
        }

        Assert.Equal("2026-11-02", source["consulted_at"]!.GetValue<string>());
    }

    [Fact]
    [Trait("spec", "SPEC-0033:CT-01")]
    public async Task Fixtures_de_anuncio_e_descricao_viram_MercadoLivreItemDetails_com_todos_os_campos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado as fixtures item-details e item-description servidas sem alteração, para o vendedor delas
        var item = ListingFixtures.Response("item-details.json");
        var description = ListingFixtures.Description();
        var itemId = item["id"]!.GetValue<string>();
        var seller = new MlSeller(item["seller_id"]!.GetValue<long>(), "LOJA_FIXTURE");
        var account = await MercadoLivreGateway.SeedAccountAsync(factory, seller, MlTokens.New(), DateTimeOffset.UtcNow.AddHours(6), cancellationToken);
        fake.ItemResponds(item);
        fake.DescriptionResponds(itemId, description);

        // Quando mapeadas pelo gateway
        var details = await ListingGateway.GetItemDetailsAsync(factory, account.AccountId, itemId, cancellationToken);

        // Então produzem MercadoLivreItemDetails com todos os campos do §6
        Assert.NotNull(details);
        Assert.Equal(itemId, details.Id);
        Assert.Equal(item["seller_id"]!.GetValue<long>(), details.SellerId);
        Assert.Equal(item["category_id"]!.GetValue<string>(), details.CategoryId);
        Assert.Equal(item["title"]!.GetValue<string>(), details.Title);
        Assert.Equal(item["sold_quantity"]!.GetValue<int>(), details.SoldQuantity);
        Assert.Equal(item["status"]!.GetValue<string>(), details.Status);
        Assert.Equal(item["catalog_listing"]!.GetValue<bool>(), details.CatalogListing);
        Assert.Equal(item["user_product_id"]?.GetValue<string>(), details.UserProductId);
        Assert.Equal(description["plain_text"]!.GetValue<string>(), details.Description);
        Assert.Equal(DateTimeOffset.Parse(item["last_updated"]!.GetValue<string>(), CultureInfo.InvariantCulture), details.LastUpdated);
        Assert.Equal(
            item["attributes"]!.AsArray().Select(attribute => new MercadoLivreItemAttribute(
                attribute!["id"]!.GetValue<string>(),
                attribute["name"]!.GetValue<string>(),
                attribute["value_id"]?.GetValue<string>(),
                attribute["value_name"]?.GetValue<string>())),
            details.Attributes);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:CT-01")]
    public async Task Fixtures_de_categoria_e_atributos_viram_MercadoLivreCategory_com_todos_os_campos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // Dado as fixtures category e category-attributes servidas sem alteração
        var category = ListingFixtures.Category();
        var attributes = ListingFixtures.CategoryAttributes().AsArray();
        fake.CategoryResponds(category, attributes);

        // Quando mapeadas pelo gateway
        var mapped = await ListingGateway.GetCategoryAsync(factory, account.AccountId, category["id"]!.GetValue<string>(), cancellationToken);

        // Então produzem MercadoLivreCategory com todos os campos do §6
        Assert.NotNull(mapped);
        Assert.Equal(category["id"]!.GetValue<string>(), mapped.Id);
        Assert.Equal(category["name"]!.GetValue<string>(), mapped.Name);
        Assert.Equal(category["settings"]!["max_title_length"]!.GetValue<int>(), mapped.MaxTitleLength);
        Assert.Equal(category["settings"]!["max_description_length"]!.GetValue<int>(), mapped.MaxDescriptionLength);
        Assert.Equal(attributes.Select(Expected).Select(Describe), mapped.Attributes.Select(Describe));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:CT-02")]
    public async Task Editor_monta_os_corpos_do_PUT_do_item_e_do_PUT_da_descricao_iguais_as_fixtures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o anúncio das fixtures, com descrição
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);

        // Quando o editor aplica as mudanças das fixtures de requisição
        await ListingGateway.ApplyAsync(factory, account.AccountId, itemId, ListingFixtures.ChangesFromRequestFixtures(), cancellationToken);

        // Então os corpos são iguais a item-update-request.json e item-description-request.json (campos e tipos)
        ListingEditorTests.AssertJsonEqual(
            ListingFixtures.ItemUpdateRequest(),
            MercadoLivreListingFake.BodyOf(Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)))));
        ListingEditorTests.AssertJsonEqual(
            ListingFixtures.DescriptionRequest(),
            MercadoLivreListingFake.BodyOf(Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.DescriptionPath(itemId)))));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:CT-02")]
    public async Task Editor_monta_o_corpo_do_POST_da_descricao_igual_a_fixture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o anúncio das fixtures, sem descrição
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account, withDescription: false);
        fake.WritesSucceed(itemId);

        // Quando o editor aplica só a descrição da fixture de requisição
        await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(null, ListingFixtures.ChangesFromRequestFixtures().Description, null), cancellationToken);

        // Então o corpo do POST é igual a item-description-request.json
        ListingEditorTests.AssertJsonEqual(
            ListingFixtures.DescriptionRequest(),
            MercadoLivreListingFake.BodyOf(Assert.Single(fake.CallsTo("POST", MercadoLivreListingFake.DescriptionPath(itemId)))));
    }

    /// <summary>Atributo esperado a partir de um item de <c>category-attributes.json</c> (regras do §6).</summary>
    private static MercadoLivreCategoryAttribute Expected(JsonNode? attribute)
    {
        var tags = attribute!["tags"]?.AsObject() ?? [];
        bool Tag(string name) => tags[name]?.GetValue<bool>() == true;
        return new MercadoLivreCategoryAttribute(
            attribute["id"]!.GetValue<string>(),
            attribute["name"]!.GetValue<string>(),
            attribute["value_type"]!.GetValue<string>(),
            Tag("required") || Tag("catalog_required"),
            Tag("read_only"),
            Tag("hidden"),
            [.. (attribute["values"]?.AsArray() ?? []).Select(value => new MercadoLivreAttributeValue(
                value!["id"]!.GetValue<string>(), value["name"]!.GetValue<string>()))],
            [.. (attribute["allowed_units"]?.AsArray() ?? []).Select(unit => unit!["id"]!.GetValue<string>())]);
    }

    /// <summary>Texto comparável de um atributo (as listas internas não têm igualdade estrutural no record).</summary>
    private static string Describe(MercadoLivreCategoryAttribute attribute) =>
        $"{attribute.Id}|{attribute.Name}|{attribute.ValueType}|required={attribute.Required}|read_only={attribute.ReadOnly}"
        + $"|hidden={attribute.Hidden}|values=[{string.Join(", ", attribute.Values)}]|units=[{string.Join(", ", attribute.AllowedUnits)}]";
}
