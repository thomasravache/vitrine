using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Publicação das mudanças aprovadas (<see cref="IMercadoLivreListingEditor"/>, SPEC-0033): corpos das escritas, ordem,
/// resultado por campo, métrica e spans; e as pré-condições locais (<see cref="ListingChangeRules"/>) que impedem o envio.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingEditorTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0033:IT-03")]
    public async Task Titulo_descricao_e_atributos_validos_vao_num_PUT_do_item_e_num_PUT_da_descricao_existente()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);
        using var metrics = new ListingEditsListener(factory.Services.GetRequiredService<IMeterFactory>());
        using var spans = new ApiSpansListener();

        // Dado um anúncio ativo, sem vendas, com descrição existente
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe título, descrição e dois atributos válidos (lista e livre)
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, ListingFixtures.ChangesFromRequestFixtures(), cancellationToken);

        // Então há exatamente 1 PUT /items/{id} com title e attributes iguais a item-update-request.json
        var itemPut = Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
        AssertJsonEqual(ListingFixtures.ItemUpdateRequest(), MercadoLivreListingFake.BodyOf(itemPut));
        Assert.Equal($"Bearer {account.Tokens.AccessToken}", MercadoLivreListingFake.AuthorizationOf(itemPut));

        // E depois 1 PUT /items/{id}/description?api_version=2 com plain_text, sem POST
        var descriptionPut = Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.DescriptionPath(itemId)));
        Assert.Equal("2", MercadoLivreFake.QueryOf(descriptionPut).GetValueOrDefault("api_version"));
        AssertJsonEqual(ListingFixtures.DescriptionRequest(), MercadoLivreListingFake.BodyOf(descriptionPut));
        Assert.Empty(fake.CallsTo("POST", MercadoLivreListingFake.DescriptionPath(itemId)));
        Assert.Equal([itemPut.Guid, descriptionPut.Guid], fake.Writes(itemId).Select(entry => entry.Guid));

        // E todos os campos voltam Applied
        Assert.IsType<FieldResult.Applied>(result.Title);
        Assert.IsType<FieldResult.Applied>(result.Description);
        Assert.Equal(["COLOR", "MODEL"], result.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Attributes.Values, field => Assert.IsType<FieldResult.Applied>(field));

        // E listing_edits{field, result=applied} foi incrementada para cada campo
        Assert.True(metrics.Total("title", "applied") == 1, $"listing_edits{{title, applied}} esperado 1. {metrics.Describe()}");
        Assert.True(metrics.Total("description", "applied") == 1, $"listing_edits{{description, applied}} esperado 1. {metrics.Describe()}");
        Assert.True(metrics.Total("attributes", "applied") >= 1, $"listing_edits{{attributes, applied}} esperado. {metrics.Describe()}");
        Assert.Equal(0, metrics.Total(result: "rejected") + metrics.Total(result: "not_attempted"));

        // E os spans das chamadas têm o caminho modelo, sem o id do anúncio nem da categoria
        var endpoints = spans.EndpointsFor(account.AccountId);
        Assert.Contains("/items/{id}", endpoints);
        Assert.Contains("/items/{id}/description", endpoints);
        Assert.All(endpoints, endpoint =>
        {
            Assert.DoesNotContain(itemId, endpoint, StringComparison.Ordinal);
            Assert.DoesNotContain(ListingFixtures.CategoryId, endpoint, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-03")]
    public async Task Anuncio_sem_descricao_recebe_a_descricao_por_POST()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado um anúncio ativo sem descrição (GET /description 404)
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account, withDescription: false);
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe título, descrição e atributos válidos
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, ListingFixtures.ChangesFromRequestFixtures(), cancellationToken);

        // Então a descrição vai por POST /items/{id}/description, depois do PUT do item, e não há PUT da descrição
        var itemPut = Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
        var descriptionPost = Assert.Single(fake.CallsTo("POST", MercadoLivreListingFake.DescriptionPath(itemId)));
        AssertJsonEqual(ListingFixtures.DescriptionRequest(), MercadoLivreListingFake.BodyOf(descriptionPost));
        Assert.Empty(fake.CallsTo("PUT", MercadoLivreListingFake.DescriptionPath(itemId)));
        Assert.Equal([itemPut.Guid, descriptionPost.Guid], fake.Writes(itemId).Select(entry => entry.Guid));

        // E todos os campos voltam Applied
        Assert.IsType<FieldResult.Applied>(result.Title);
        Assert.IsType<FieldResult.Applied>(result.Description);
        Assert.All(result.Attributes.Values, field => Assert.IsType<FieldResult.Applied>(field));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-04")]
    public async Task Item_com_vendas_nao_envia_o_titulo_e_o_PUT_leva_so_os_atributos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado um anúncio ativo com vendas
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account, edit: item => item["sold_quantity"] = 12);
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe título e atributos
        var changes = ListingFixtures.ChangesFromRequestFixtures() with { Description = null };
        var result = await ListingGateway.ApplyAsync(factory, account.AccountId, itemId, changes, cancellationToken);

        // Então o título fica NotAttempted(TITLE_LOCKED_SOLD)
        Assert.Equal(new FieldResult.NotAttempted("TITLE_LOCKED_SOLD"), result.Title);

        // E o PUT leva só os atributos, que voltam Applied
        var put = Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
        var expected = ListingFixtures.ItemUpdateRequest().AsObject();
        expected.Remove("title");
        AssertJsonEqual(expected, MercadoLivreListingFake.BodyOf(put));
        Assert.All(result.Attributes.Values, field => Assert.IsType<FieldResult.Applied>(field));
        Assert.Null(result.Description);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-04")]
    public async Task Item_pausado_nao_recebe_nenhuma_escrita()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado um anúncio pausado
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account, edit: item => item["status"] = "paused");
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe título, descrição e atributos válidos
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, ListingFixtures.ChangesFromRequestFixtures(), cancellationToken);

        // Então todos os campos ficam NotAttempted(ITEM_NOT_ACTIVE) e não há nenhuma escrita
        var notActive = new FieldResult.NotAttempted("ITEM_NOT_ACTIVE");
        Assert.Equal(notActive, result.Title);
        Assert.Equal(notActive, result.Description);
        Assert.Equal(["COLOR", "MODEL"], result.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Attributes.Values, field => Assert.Equal(notActive, field));
        Assert.Empty(fake.Writes(itemId));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-04")]
    public async Task Descricao_com_HTML_nao_chama_o_endpoint_de_descricao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado um anúncio ativo com descrição
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe só uma descrição com HTML
        var result = await ListingGateway.ApplyAsync(
            factory, account.AccountId, itemId, new ListingChanges(null, "Fone <b>bluetooth</b> sem fio.", null), cancellationToken);

        // Então a descrição fica NotAttempted(DESCRIPTION_INVALID) e não há nenhuma escrita
        Assert.Equal(new FieldResult.NotAttempted("DESCRIPTION_INVALID"), result.Description);
        Assert.Null(result.Title);
        Assert.Empty(result.Attributes);
        Assert.Empty(fake.Writes(itemId));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-04")]
    public async Task Atributo_read_only_e_valor_fora_da_lista_ficam_fora_do_corpo_do_PUT()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado um anúncio ativo
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe um atributo read_only, um valor fora da lista e um atributo livre válido
        var result = await ListingGateway.ApplyAsync(
            factory,
            account.AccountId,
            itemId,
            new ListingChanges(null, null,
            [
                new AttributeChange("ITEM_CONDITION", "2230581", null),
                new AttributeChange("COLOR", "99999", null),
                new AttributeChange("MODEL", null, "BT-500 Pro"),
            ]),
            cancellationToken);

        // Então os inválidos ficam NotAttempted(ATTRIBUTE_*) e o válido é Applied
        Assert.Equal(new FieldResult.NotAttempted("ATTRIBUTE_NOT_EDITABLE"), result.Attributes["ITEM_CONDITION"]);
        Assert.Equal(new FieldResult.NotAttempted("ATTRIBUTE_VALUE_NOT_ALLOWED"), result.Attributes["COLOR"]);
        Assert.IsType<FieldResult.Applied>(result.Attributes["MODEL"]);

        // E o PUT leva só o atributo válido
        var put = Assert.Single(fake.CallsTo("PUT", MercadoLivreListingFake.ItemPath(itemId)));
        AssertJsonEqual(
            new JsonObject { ["attributes"] = new JsonArray(new JsonObject { ["id"] = "MODEL", ["value_name"] = "BT-500 Pro" }) },
            MercadoLivreListingFake.BodyOf(put));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-04")]
    public async Task Atributo_repetido_lanca_ArgumentException_sem_nenhuma_escrita()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreListingFake(factory.ExternalServices);

        // Dado um anúncio ativo
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = ListingGateway.SetUpItem(fake, account);
        fake.WritesSucceed(itemId);

        // Quando ApplyAsync recebe o mesmo atributo duas vezes (emenda v1.1)
        var changes = new ListingChanges(
            ListingFixtures.ChangesFromRequestFixtures().Title,
            null,
            [new AttributeChange("MODEL", null, "BT-500 Pro"), new AttributeChange("MODEL", null, "BT-600")]);

        // Então lança ArgumentException antes de qualquer escrita
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => ListingGateway.ApplyAsync(factory, account.AccountId, itemId, changes, cancellationToken));
        Assert.Empty(fake.Writes(itemId));
    }

    /// <summary>Igualdade de JSON (campos, tipos e valores), com os dois corpos na mensagem de falha.</summary>
    internal static void AssertJsonEqual(JsonNode expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Corpo diferente do esperado.\nEsperado: {expected.ToJsonString()}\nRecebido: {actual?.ToJsonString() ?? "(vazio)"}");
}
