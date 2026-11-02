using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0034:IT-01 — <c>GET /items/{id}</c>, <c>GET /items/{id}/description</c>, <c>GET /categories/{id}</c> e
/// <c>/categories/{id}/attributes</c> no formato do contrato (seção 6), com 404 no formato do ML e 401 com token inválido.
/// </summary>
public sealed class SimulatorItemDetailsTests
{
    private static readonly string[] ItemFields =
    [
        "id", "seller_id", "category_id", "title", "price", "currency_id", "available_quantity", "sold_quantity", "status",
        "catalog_listing", "user_product_id", "permalink", "thumbnail", "secure_thumbnail", "listing_type_id",
        "last_updated", "attributes",
    ];

    private static long Number(string itemId) => long.Parse(itemId.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture);

    private static JsonElement Attribute(JsonElement attributes, string id)
    {
        var found = attributes.EnumerateArray().Where(a => a.GetProperty("id").GetString() == id).ToList();
        Assert.True(found.Count == 1, $"Esperava 1 atributo {id}; vieram {found.Count}: {attributes}");
        return found[0];
    }

    private static bool Tag(JsonElement attribute, string tag) =>
        attribute.GetProperty("tags").TryGetProperty(tag, out var value) && value.ValueKind == JsonValueKind.True;

    private static List<string> Names(JsonElement attribute, string list) =>
        [.. attribute.GetProperty(list).EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

    private static void AssertMlError(SimulatorResponse response, HttpStatusCode status)
    {
        Assert.True(response.Status == status, $"Esperava {(int)status}; veio {(int)response.Status}: {response.Body}");
        var json = response.Json;
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.String, json.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("cause").ValueKind);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-01")]
    public async Task Detalhe_do_anuncio_traz_os_campos_do_contrato_com_ou_sem_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];

        // Quando GET /items/{id} sem token (recurso público) e com o token do dono
        var anonymous = await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken);
        var authenticated = await client.GetAsync($"/items/{id}", seller.AccessToken, cancellationToken);

        // Então ambos respondem 200 com os campos do §6
        Assert.True(anonymous.Status == HttpStatusCode.OK, $"GET /items/{id} sem token respondeu {(int)anonymous.Status}: {anonymous.Body}");
        Assert.True(authenticated.Status == HttpStatusCode.OK, $"GET /items/{id} com token respondeu {(int)authenticated.Status}: {authenticated.Body}");
        var item = authenticated.Json;
        var missing = ItemFields.Where(field => !item.TryGetProperty(field, out _)).ToList();
        Assert.True(missing.Count == 0, $"Campos ausentes: {string.Join(", ", missing)}\n{item}");
        Assert.Equal(id, item.GetProperty("id").GetString());
        Assert.Equal(seller.Id, item.GetProperty("seller_id").GetInt64());
        Assert.StartsWith("MLB9000", item.GetProperty("category_id").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Number, item.GetProperty("price").ValueKind);
        Assert.Equal("BRL", item.GetProperty("currency_id").GetString());
        Assert.Equal(0, item.GetProperty("sold_quantity").GetInt32());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.False, item.GetProperty("catalog_listing").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("user_product_id").ValueKind);
        Assert.True(DateTimeOffset.TryParse(item.GetProperty("last_updated").GetString(), CultureInfo.InvariantCulture, out _));

        // E os atributos têm id, name, value_id e value_name (BRAND e ITEM_CONDITION preenchidos)
        var attributes = item.GetProperty("attributes");
        foreach (var attribute in attributes.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, attribute.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, attribute.GetProperty("name").ValueKind);
            Assert.True(attribute.TryGetProperty("value_id", out _), $"Atributo sem value_id: {attribute}");
            Assert.True(attribute.TryGetProperty("value_name", out _), $"Atributo sem value_name: {attribute}");
        }

        Assert.Equal("Marca Simulada", Attribute(attributes, "BRAND").GetProperty("value_name").GetString());
        Assert.Equal("Novo", Attribute(attributes, "ITEM_CONDITION").GetProperty("value_name").GetString());

        // E o 1º ativo tem vendas e o 2º é de catálogo
        var first = await client.GetAsync($"/items/{seller.Active[0]}", cancellationToken: cancellationToken);
        var second = await client.GetAsync($"/items/{seller.Active[1]}", cancellationToken: cancellationToken);
        Assert.Equal(12, first.Json.GetProperty("sold_quantity").GetInt32());
        Assert.Equal(JsonValueKind.True, second.Json.GetProperty("catalog_listing").ValueKind);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-01")]
    public async Task Detalhe_com_attributes_devolve_so_os_campos_pedidos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);

        var response = await client.GetAsync($"/items/{seller.Active[2]}?attributes=id,category_id,attributes", seller.AccessToken, cancellationToken);

        Assert.True(response.Status == HttpStatusCode.OK, $"Respondeu {(int)response.Status}: {response.Body}");
        var fields = response.Json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["attributes", "category_id", "id"], fields);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-01")]
    public async Task Anuncio_inexistente_da_404_no_formato_do_ML_e_token_invalido_da_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);

        AssertMlError(await client.GetAsync("/items/MLB1999999999", cancellationToken: cancellationToken), HttpStatusCode.NotFound);
        AssertMlError(await client.GetAsync("/items/MLB1999999999/description", cancellationToken: cancellationToken), HttpStatusCode.NotFound);
        AssertMlError(await client.GetAsync($"/items/{seller.Active[2]}", "APP_USR-token-invalido", cancellationToken), HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-01")]
    public async Task Descricao_existe_nos_anuncios_pares_e_da_404_nos_impares()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var even = seller.Active.First(id => Number(id) % 2 == 0);
        var odd = seller.Active.First(id => Number(id) % 2 == 1);

        // Anúncio par: 200 com plain_text, last_updated e date_created
        var found = await client.GetAsync($"/items/{even}/description", cancellationToken: cancellationToken);
        Assert.True(found.Status == HttpStatusCode.OK, $"GET /items/{even}/description respondeu {(int)found.Status}: {found.Body}");
        var description = found.Json;
        Assert.Contains("Cor: Preto", description.GetProperty("plain_text").GetString(), StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(description.GetProperty("last_updated").GetString(), CultureInfo.InvariantCulture, out _));
        Assert.True(DateTimeOffset.TryParse(description.GetProperty("date_created").GetString(), CultureInfo.InvariantCulture, out _));

        // Anúncio ímpar: 404 no formato do ML
        AssertMlError(await client.GetAsync($"/items/{odd}/description", cancellationToken: cancellationToken), HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:IT-01")]
    [InlineData("MLB90001", "Eletrônicos e acessórios (simulado)")]
    [InlineData("MLB90002", "Casa e cozinha (simulado)")]
    [InlineData("MLB90003", "Bolsas e mochilas (simulado)")]
    public async Task Categoria_traz_nome_e_limites(string categoryId, string name)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        var response = await client.GetAsync($"/categories/{categoryId}", cancellationToken: cancellationToken);

        Assert.True(response.Status == HttpStatusCode.OK, $"GET /categories/{categoryId} respondeu {(int)response.Status}: {response.Body}");
        var category = response.Json;
        Assert.Equal(categoryId, category.GetProperty("id").GetString());
        Assert.Equal(name, category.GetProperty("name").GetString());
        Assert.Equal(60, category.GetProperty("settings").GetProperty("max_title_length").GetInt32());
        Assert.Equal(50_000, category.GetProperty("settings").GetProperty("max_description_length").GetInt32());
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-01")]
    public async Task Atributos_das_categorias_trazem_tipos_tags_valores_e_unidades()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        var lists = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var categoryId in new[] { "MLB90001", "MLB90002", "MLB90003" })
        {
            var response = await client.GetAsync($"/categories/{categoryId}/attributes", cancellationToken: cancellationToken);
            Assert.True(response.Status == HttpStatusCode.OK, $"GET /categories/{categoryId}/attributes respondeu {(int)response.Status}: {response.Body}");
            lists[categoryId] = response.Json;

            // Atributos comuns
            var attributes = response.Json;
            var brand = Attribute(attributes, "BRAND");
            Assert.Equal("string", brand.GetProperty("value_type").GetString());
            Assert.True(Tag(brand, "required"), $"BRAND sem tags.required em {categoryId}: {brand}");
            var model = Attribute(attributes, "MODEL");
            Assert.Equal("string", model.GetProperty("value_type").GetString());
            Assert.False(Tag(model, "required"), $"MODEL não é obrigatório em {categoryId}: {model}");
            var color = Attribute(attributes, "COLOR");
            Assert.Equal("list", color.GetProperty("value_type").GetString());
            Assert.True(Tag(color, "required"), $"COLOR sem tags.required em {categoryId}: {color}");
            Assert.Equal(["Preto", "Branco", "Azul", "Vermelho"], Names(color, "values"));
            var condition = Attribute(attributes, "ITEM_CONDITION");
            Assert.Equal("list", condition.GetProperty("value_type").GetString());
            Assert.True(Tag(condition, "read_only"), $"ITEM_CONDITION sem tags.read_only em {categoryId}: {condition}");
            Assert.Equal(["Novo"], Names(condition, "values"));
            var source = Attribute(attributes, "PRODUCT_DATA_SOURCE");
            Assert.Equal("string", source.GetProperty("value_type").GetString());
            Assert.True(Tag(source, "hidden"), $"PRODUCT_DATA_SOURCE sem tags.hidden em {categoryId}: {source}");

            // values e allowed_units com { id, name }
            foreach (var attribute in attributes.EnumerateArray())
            {
                foreach (var list in new[] { "values", "allowed_units" })
                {
                    if (attribute.TryGetProperty(list, out var entries))
                    {
                        Assert.All(entries.EnumerateArray(), entry =>
                        {
                            Assert.Equal(JsonValueKind.String, entry.GetProperty("id").ValueKind);
                            Assert.Equal(JsonValueKind.String, entry.GetProperty("name").ValueKind);
                        });
                    }
                }
            }
        }

        // Específicos de cada categoria
        var wireless = Attribute(lists["MLB90001"], "IS_WIRELESS");
        Assert.Equal("boolean", wireless.GetProperty("value_type").GetString());
        Assert.Equal(["Sim", "Não"], Names(wireless, "values"));

        var capacity = Attribute(lists["MLB90002"], "CAPACITY");
        Assert.Equal("number_unit", capacity.GetProperty("value_type").GetString());
        Assert.Equal(["mL", "L"], Names(capacity, "allowed_units"));
        var kitchenMaterial = Attribute(lists["MLB90002"], "MATERIAL");
        Assert.Equal("list", kitchenMaterial.GetProperty("value_type").GetString());
        Assert.Equal(["Aço inoxidável", "Cerâmica", "Plástico"], Names(kitchenMaterial, "values"));

        var laptop = Attribute(lists["MLB90003"], "LAPTOP_SIZE");
        Assert.Equal("number_unit", laptop.GetProperty("value_type").GetString());
        Assert.Equal(["pol"], Names(laptop, "allowed_units"));
        var bagMaterial = Attribute(lists["MLB90003"], "MATERIAL");
        Assert.Equal("list", bagMaterial.GetProperty("value_type").GetString());
        Assert.Equal(["Poliéster", "Nylon", "Couro sintético"], Names(bagMaterial, "values"));

        Assert.DoesNotContain(lists["MLB90001"].EnumerateArray(), a => a.GetProperty("id").GetString() is "CAPACITY" or "LAPTOP_SIZE");
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-01")]
    public async Task Categoria_inexistente_da_404_no_formato_do_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        AssertMlError(await client.GetAsync("/categories/MLB99999", cancellationToken: cancellationToken), HttpStatusCode.NotFound);
        AssertMlError(await client.GetAsync("/categories/MLB99999/attributes", cancellationToken: cancellationToken), HttpStatusCode.NotFound);
    }
}
