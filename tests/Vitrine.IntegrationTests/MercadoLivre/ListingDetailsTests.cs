using System.Globalization;
using System.Text.Json.Nodes;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Leitura detalhada do anúncio e da categoria (<see cref="IMercadoLivreListingDetails"/>, SPEC-0033), com o ML simulado
/// pelo WireMock a partir das fixtures sintéticas.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingDetailsTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0033:IT-01")]
    public async Task Detalhe_do_anuncio_do_vendedor_traz_todos_os_campos_e_a_descricao_com_Bearer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado uma conta com token válido e o ML com GET /items/{id} e GET /items/{id}/description nas fixtures
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        var item = ListingFixtures.Item(itemId, account.Seller.Id);

        // Quando GetItemDetailsAsync é chamado
        var details = await ListingGateway.GetItemDetailsAsync(factory, account.AccountId, itemId, cancellationToken);

        // Então devolve todos os campos mapeados, com a descrição em texto puro
        Assert.NotNull(details);
        Assert.Equal(itemId, details.Id);
        Assert.Equal(account.Seller.Id, details.SellerId);
        Assert.Equal(item["category_id"]!.GetValue<string>(), details.CategoryId);
        Assert.Equal(item["title"]!.GetValue<string>(), details.Title);
        Assert.Equal(item["sold_quantity"]!.GetValue<int>(), details.SoldQuantity);
        Assert.Equal("active", details.Status);
        Assert.False(details.CatalogListing);
        Assert.Null(details.UserProductId);
        Assert.Equal(ListingFixtures.Description()["plain_text"]!.GetValue<string>(), details.Description);
        Assert.Equal(
            DateTimeOffset.Parse(item["last_updated"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            details.LastUpdated);
        Assert.Equal(item["attributes"]!.AsArray().Count, details.Attributes.Count);
        Assert.Contains(new MercadoLivreItemAttribute("COLOR", "Cor", "52049", "Preto"), details.Attributes);
        Assert.Contains(new MercadoLivreItemAttribute("MODEL", "Modelo", null, "BT-500"), details.Attributes);

        // E as duas leituras foram com Authorization: Bearer do token da conta
        var reads = fake.CallsTo("GET", MercadoLivreListingFake.ItemPath(itemId))
            .Concat(fake.CallsTo("GET", MercadoLivreListingFake.DescriptionPath(itemId)))
            .ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, entry => Assert.Equal($"Bearer {account.Tokens.AccessToken}", MercadoLivreListingFake.AuthorizationOf(entry)));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-01")]
    public async Task Anuncio_sem_descricao_tem_Description_nula()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o GET /items/{id}/description respondendo 404
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account, withDescription: false);

        // Quando GetItemDetailsAsync é chamado
        var details = await ListingGateway.GetItemDetailsAsync(factory, account.AccountId, itemId, cancellationToken);

        // Então o anúncio vem com Description nula
        Assert.NotNull(details);
        Assert.Equal(itemId, details.Id);
        Assert.Null(details.Description);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-01")]
    public async Task Anuncio_inexistente_devolve_nulo_e_o_editor_lanca_ItemNotFound_sem_escrever()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o GET /items/{id} respondendo 404
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = MercadoLivreListingFake.NewItemId();
        fake.ItemFails(itemId, 404);
        fake.DescriptionMissing(itemId);
        fake.CategoryResponds(ListingFixtures.Category(), ListingFixtures.CategoryAttributes());
        fake.WritesSucceed(itemId);

        // Quando o detalhe é lido; então é nulo
        Assert.Null(await ListingGateway.GetItemDetailsAsync(factory, account.AccountId, itemId, cancellationToken));

        // E quando o editor é chamado; então lança ItemNotFound sem nenhum PUT ou POST
        var thrown = await Assert.ThrowsAsync<MercadoLivreItemNotFoundException>(() => ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges("Fone Bluetooth Sem Fio Novo", null, null), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);
        Assert.Equal(itemId, thrown.ItemId);
        Assert.Empty(fake.Writes(itemId));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-01")]
    public async Task Anuncio_de_outro_vendedor_e_tratado_como_inexistente_e_o_editor_nao_escreve()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado o GET /items/{id} devolvendo um anúncio com seller_id de outro vendedor
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account, edit: item => item["seller_id"] = account.Seller.Id + 1);
        fake.WritesSucceed(itemId);

        // Quando o detalhe é lido; então é nulo
        Assert.Null(await ListingGateway.GetItemDetailsAsync(factory, account.AccountId, itemId, cancellationToken));

        // E quando o editor é chamado; então lança ItemNotFound sem nenhum PUT ou POST
        await Assert.ThrowsAsync<MercadoLivreItemNotFoundException>(() => ListingGateway.ApplyAsync(
            factory,
            account.AccountId,
            itemId,
            new ListingChanges("Fone Bluetooth Sem Fio Novo", "Fone bluetooth sem fio.", [new AttributeChange("MODEL", null, "BT-600")]),
            cancellationToken));
        Assert.Empty(fake.Writes(itemId));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-02")]
    public async Task Categoria_e_lida_uma_vez_mapeada_e_fica_em_cache_por_24_h()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o relógio controlado do host e uma conta com token válido por 3 dias (o relógio vai avançar 24 h)
        var clock = new AdjustableClock();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithClock(clock);
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var account = await SeedLongLivedAccountAsync(factory, cancellationToken);
        fake.CategoryResponds(ListingFixtures.Category(), ListingFixtures.CategoryAttributes());
        var categoryPath = MercadoLivreListingFake.CategoryPath(ListingFixtures.CategoryId);
        var attributesPath = MercadoLivreListingFake.CategoryAttributesPath(ListingFixtures.CategoryId);

        // Quando GetCategoryAsync é chamado duas vezes
        var first = await ListingGateway.GetCategoryAsync(factory, account.AccountId, ListingFixtures.CategoryId, cancellationToken);
        var second = await ListingGateway.GetCategoryAsync(factory, account.AccountId, ListingFixtures.CategoryId, cancellationToken);

        // Então o ML recebe 1 chamada a cada endpoint
        Assert.Single(fake.CallsTo("GET", categoryPath));
        Assert.Single(fake.CallsTo("GET", attributesPath));
        Assert.NotNull(second);

        // E o resultado tem os limites, as tags (Required inclusive por catalog_required), os valores e as unidades
        Assert.NotNull(first);
        Assert.Equal(ListingFixtures.CategoryId, first.Id);
        Assert.Equal("Fones de Ouvido", first.Name);
        Assert.Equal(60, first.MaxTitleLength);
        Assert.Equal(50000, first.MaxDescriptionLength);
        var attributes = first.Attributes.ToDictionary(attribute => attribute.Id, StringComparer.Ordinal);
        Assert.Equal(ListingFixtures.CategoryAttributes().AsArray().Count, attributes.Count);

        var color = attributes["COLOR"];
        Assert.Equal("list", color.ValueType);
        Assert.True(color.Required, "COLOR tem tags.catalog_required: Required precisa ser true.");
        Assert.False(color.ReadOnly);
        Assert.False(color.Hidden);
        Assert.Equal(
            [new MercadoLivreAttributeValue("52049", "Preto"), new MercadoLivreAttributeValue("52055", "Branco"), new MercadoLivreAttributeValue("52100", "Café")],
            color.Values);

        var model = attributes["MODEL"];
        Assert.True(model.Required, "MODEL tem tags.required.");
        Assert.Empty(model.Values);

        var battery = attributes["BATTERY_LIFE"];
        Assert.Equal("number_unit", battery.ValueType);
        Assert.False(battery.Required);
        Assert.Equal(["h", "min"], battery.AllowedUnits);

        Assert.True(attributes["ITEM_CONDITION"].ReadOnly);
        Assert.False(attributes["ITEM_CONDITION"].Hidden);
        Assert.True(attributes["PRODUCT_FEATURES"].Hidden);
        Assert.False(attributes["PRODUCT_FEATURES"].Required);

        // Quando o relógio do cache passa de 24 h e a categoria é lida de novo
        clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        var renewed = await ListingGateway.GetCategoryAsync(factory, account.AccountId, ListingFixtures.CategoryId, cancellationToken);

        // Então há nova chamada a cada endpoint
        Assert.NotNull(renewed);
        Assert.Equal(2, fake.CallsTo("GET", categoryPath).Count);
        Assert.Equal(2, fake.CallsTo("GET", attributesPath).Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-02")]
    public async Task Categoria_inexistente_devolve_nulo_e_nao_fica_em_cache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // Dado o GET /categories/{id} respondendo 404
        const string missing = "MLB90404";
        fake.CategoryMissing(missing);

        // Quando GetCategoryAsync é chamado duas vezes; então devolve nulo nas duas
        Assert.Null(await ListingGateway.GetCategoryAsync(factory, account.AccountId, missing, cancellationToken));
        Assert.Null(await ListingGateway.GetCategoryAsync(factory, account.AccountId, missing, cancellationToken));

        // E a categoria foi consultada de novo na segunda chamada (o 404 não fica em cache)
        Assert.Equal(2, fake.CallsTo("GET", MercadoLivreListingFake.CategoryPath(missing)).Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-02")]
    public async Task Categoria_sem_max_description_length_tem_limite_nulo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // Dado a categoria sem settings.max_description_length
        var category = ListingFixtures.Category().AsObject();
        category["id"] = "MLB90002";
        category["settings"]!.AsObject().Remove("max_description_length");
        fake.CategoryResponds(category, ListingFixtures.CategoryAttributes());

        // Quando GetCategoryAsync é chamado; então MaxDescriptionLength é nulo
        var mapped = await ListingGateway.GetCategoryAsync(factory, account.AccountId, "MLB90002", cancellationToken);
        Assert.NotNull(mapped);
        Assert.Null(mapped.MaxDescriptionLength);
        Assert.Equal(60, mapped.MaxTitleLength);
    }

    /// <summary>Conta com access token válido por 3 dias (o relógio do teste pode avançar 24 h sem renovar).</summary>
    internal static Task<SeededAccount> SeedLongLivedAccountAsync(ApiFactory factory, CancellationToken cancellationToken)
    {
        var tokens = MlTokens.New() with { ExpiresIn = (int)TimeSpan.FromDays(3).TotalSeconds };
        return MercadoLivreGateway.SeedAccountAsync(factory, MlSeller.New(), tokens, DateTimeOffset.UtcNow.AddDays(3), cancellationToken);
    }
}
