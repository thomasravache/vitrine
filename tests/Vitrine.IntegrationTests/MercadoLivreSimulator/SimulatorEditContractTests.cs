using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// Lado provedor do contrato SPEC-0033@1 no simulador: SPEC-0034:CT-01 (respostas com os campos e tipos das fixtures
/// de <c>tests/Fixtures/MercadoLivre/</c>) e SPEC-0034:CT-02 (as fixtures de requisição são aceitas e aplicadas).
/// </summary>
public sealed class SimulatorEditContractTests
{
    /// <summary>Campos do detalhe do anúncio lidos pelo gateway (SPEC-0033, seção 6).</summary>
    private static readonly string[] ItemFields =
    [
        "id", "seller_id", "category_id", "title", "sold_quantity", "status", "catalog_listing", "user_product_id",
        "permalink", "last_updated", "attributes",
    ];

    private static readonly string[] ItemAttributeFields = ["id", "name", "value_id", "value_name"];

    private static readonly string[] DescriptionFields = ["plain_text", "last_updated", "date_created"];

    private static readonly string[] CategoryFields = ["id", "name", "settings.max_title_length", "settings.max_description_length"];

    private static readonly string[] ErrorFields = ["message", "error", "status", "cause"];

    private static readonly string[] ErrorWithCauseFields = ["message", "error", "status", "cause", "cause.0.code", "cause.0.message"];

    private static long Number(string itemId) => long.Parse(itemId.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture);

    private static JsonElement Response(string fixture) => MlFixtures.Load(fixture).GetProperty("response");

    private static JsonElement Request(string fixture) => MlFixtures.Load(fixture).GetProperty("request");

    private static JsonElement ById(JsonElement list, string id)
    {
        var found = list.EnumerateArray().Where(e => e.GetProperty("id").GetString() == id).ToList();
        Assert.True(found.Count == 1, $"Esperava 1 elemento {id}; vieram {found.Count}: {list}");
        return found[0];
    }

    private static async Task<JsonElement> OkJsonAsync(Task<SimulatorResponse> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        Assert.True(response.Status == expected, $"Esperava {(int)expected}; veio {(int)response.Status}: {response.Body}");
        return response.Json;
    }

    private static async Task AssertStatusAsync(Task<SimulatorResponse> request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await request;
        Assert.True(response.Status == expected, $"Esperava {(int)expected}; veio {(int)response.Status}: {response.Body}");
    }

    [Fact]
    [Trait("spec", "SPEC-0034:CT-01")]
    public async Task Detalhe_descricao_e_categoria_do_simulador_seguem_as_fixtures_da_SPEC_0033()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];

        // item-details.json: campos do item e de cada tipo de atributo (lista com value_id; texto com value_id nulo)
        var item = await OkJsonAsync(client.GetAsync($"/items/{id}", seller.AccessToken, cancellationToken));
        var fixtureItem = Response("item-details.json");
        MlFixtures.AssertSameShape("item-details.json", fixtureItem, item, ItemFields);
        var fixtureAttributes = fixtureItem.GetProperty("attributes");
        MlFixtures.AssertSameShape(
            "item-details.json[ITEM_CONDITION]", ById(fixtureAttributes, "ITEM_CONDITION"), ById(item.GetProperty("attributes"), "ITEM_CONDITION"),
            ItemAttributeFields);
        MlFixtures.AssertSameShape(
            "item-details.json[MODEL]", ById(fixtureAttributes, "MODEL"), ById(item.GetProperty("attributes"), "BRAND"), ItemAttributeFields);

        // item-description.json
        var withDescription = seller.Active.First(i => Number(i) % 2 == 0);
        var description = await OkJsonAsync(client.GetAsync($"/items/{withDescription}/description", cancellationToken: cancellationToken));
        MlFixtures.AssertSameShape("item-description.json", Response("item-description.json"), description, DescriptionFields);

        // category.json
        var categoryId = item.GetProperty("category_id").GetString();
        var category = await OkJsonAsync(client.GetAsync($"/categories/{categoryId}", cancellationToken: cancellationToken));
        MlFixtures.AssertSameShape("category.json", Response("category.json"), category, CategoryFields);

        // category-attributes.json: lista (COLOR), texto obrigatório (MODEL × BRAND), read_only, hidden e number_unit
        var fixtureCategory = Response("category-attributes.json");
        var electronics = await OkJsonAsync(client.GetAsync("/categories/MLB90001/attributes", cancellationToken: cancellationToken));
        var kitchen = await OkJsonAsync(client.GetAsync("/categories/MLB90002/attributes", cancellationToken: cancellationToken));
        MlFixtures.AssertSameShape(
            "category-attributes.json[COLOR]", ById(fixtureCategory, "COLOR"), ById(electronics, "COLOR"),
            "id", "name", "value_type", "tags", "values", "values.0.id", "values.0.name");
        MlFixtures.AssertSameShape(
            "category-attributes.json[MODEL]", ById(fixtureCategory, "MODEL"), ById(electronics, "BRAND"),
            "id", "name", "value_type", "tags", "tags.required");
        MlFixtures.AssertSameShape(
            "category-attributes.json[ITEM_CONDITION]", ById(fixtureCategory, "ITEM_CONDITION"), ById(electronics, "ITEM_CONDITION"),
            "id", "tags.read_only", "values", "values.0.id", "values.0.name");
        MlFixtures.AssertSameShape(
            "category-attributes.json[PRODUCT_FEATURES]", ById(fixtureCategory, "PRODUCT_FEATURES"), ById(electronics, "PRODUCT_DATA_SOURCE"),
            "id", "value_type", "tags.hidden");
        MlFixtures.AssertSameShape(
            "category-attributes.json[BATTERY_LIFE]", ById(fixtureCategory, "BATTERY_LIFE"), ById(kitchen, "CAPACITY"),
            "id", "value_type", "allowed_units", "allowed_units.0.id", "allowed_units.0.name");
        MlFixtures.AssertSameShape(
            "category-attributes.json[IS_WIRELESS]", ById(fixtureCategory, "IS_WIRELESS"), ById(electronics, "IS_WIRELESS"),
            "id", "value_type", "values", "values.0.id", "values.0.name");
    }

    [Fact]
    [Trait("spec", "SPEC-0034:CT-01")]
    public async Task Erros_de_escrita_do_simulador_seguem_as_fixtures_da_SPEC_0033()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var token = seller.AccessToken;

        // error-title-not-modifiable.json: título no 1º ativo (com vendas)
        var title = await OkJsonAsync(
            client.SendJsonAsync(HttpMethod.Put, $"/items/{seller.Active[0]}", new { title = "Título bloqueado" }, token, cancellationToken),
            HttpStatusCode.BadRequest);
        MlFixtures.AssertSameShape("error-title-not-modifiable.json", Response("error-title-not-modifiable.json"), title, ErrorWithCauseFields);

        // error-description-invalid.json: descrição com HTML
        var withDescription = seller.Active.Skip(2).First(i => Number(i) % 2 == 0);
        var description = await OkJsonAsync(
            client.SendJsonAsync(
                HttpMethod.Put, $"/items/{withDescription}/description?api_version=2", new { plain_text = "<b>Fone</b>" }, token, cancellationToken),
            HttpStatusCode.BadRequest);
        MlFixtures.AssertSameShape("error-description-invalid.json", Response("error-description-invalid.json"), description, ErrorWithCauseFields);

        // error-item-not-active.json: anúncio pausado
        var paused = await OkJsonAsync(
            client.SendJsonAsync(HttpMethod.Put, $"/items/{seller.Paused[0]}", new { title = "Título em pausado" }, token, cancellationToken),
            HttpStatusCode.BadRequest);
        MlFixtures.AssertSameShape("error-item-not-active.json", Response("error-item-not-active.json"), paused, ErrorWithCauseFields);

        // error-conflict.json: 409 forçado pelo controle
        var editable = seller.Active[2];
        var fail = await client.PostJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{seller.Id}/items/{editable}/fail-next-write"),
            new { target = "item", status = 409 },
            cancellationToken);
        Assert.True(fail.Status == HttpStatusCode.NoContent, $"fail-next-write respondeu {(int)fail.Status}: {fail.Body}");
        var conflict = await OkJsonAsync(
            client.SendJsonAsync(HttpMethod.Put, $"/items/{editable}", new { title = "Título em conflito" }, token, cancellationToken),
            HttpStatusCode.Conflict);
        MlFixtures.AssertSameShape("error-conflict.json", Response("error-conflict.json"), conflict, ErrorFields);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:CT-02")]
    public async Task Fixture_item_update_request_e_aceita_e_aplicada_num_anuncio_editavel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];
        var request = Request("item-update-request.json");

        // Quando o corpo da fixture é enviado como está
        await AssertStatusAsync(client.SendRawJsonAsync(HttpMethod.Put, $"/items/{id}", request.GetRawText(), seller.AccessToken, cancellationToken));

        // Então título, COLOR (value_id) e MODEL (value_name) foram aplicados
        var item = await OkJsonAsync(client.GetAsync($"/items/{id}", cancellationToken: cancellationToken));
        Assert.Equal(request.GetProperty("title").GetString(), item.GetProperty("title").GetString());
        var attributes = item.GetProperty("attributes");
        foreach (var wanted in request.GetProperty("attributes").EnumerateArray())
        {
            var actual = ById(attributes, wanted.GetProperty("id").GetString()!);
            foreach (var field in new[] { "value_id", "value_name" })
            {
                if (wanted.TryGetProperty(field, out var value))
                {
                    Assert.Equal(value.GetString(), actual.GetProperty(field).GetString());
                }
            }
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0034:CT-02")]
    public async Task Fixture_item_description_request_e_aceita_no_POST_e_no_PUT()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var request = Request("item-description-request.json");
        var text = request.GetProperty("plain_text").GetString();
        var without = seller.Active.Skip(2).First(i => Number(i) % 2 == 1);
        var withText = seller.Active.Skip(2).First(i => Number(i) % 2 == 0);

        // POST num anúncio sem descrição: 201 e aplicada
        await AssertStatusAsync(
            client.SendRawJsonAsync(HttpMethod.Post, $"/items/{without}/description", request.GetRawText(), seller.AccessToken, cancellationToken),
            HttpStatusCode.Created);
        var created = await OkJsonAsync(client.GetAsync($"/items/{without}/description", cancellationToken: cancellationToken));
        Assert.Equal(text, created.GetProperty("plain_text").GetString());

        // PUT ?api_version=2 num anúncio com descrição: 200 e aplicada
        await AssertStatusAsync(client.SendRawJsonAsync(
            HttpMethod.Put, $"/items/{withText}/description?api_version=2", request.GetRawText(), seller.AccessToken, cancellationToken));
        var updated = await OkJsonAsync(client.GetAsync($"/items/{withText}/description", cancellationToken: cancellationToken));
        Assert.Equal(text, updated.GetProperty("plain_text").GetString());
    }
}
