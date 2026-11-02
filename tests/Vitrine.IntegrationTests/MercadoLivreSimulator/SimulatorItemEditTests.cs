using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0034:IT-02 e IT-03 — <c>PUT /items/{id}</c> com as regras do ML (título bloqueado, atributo não editável ou fora
/// da lista, item não ativo, permissão, atomicidade) e criação/atualização da descrição.
/// </summary>
public sealed class SimulatorItemEditTests
{
    /// <summary><c>value_id</c> de "Preto" em <c>COLOR</c> (o mesmo da fixture <c>category-attributes.json</c>).</summary>
    private const string Preto = "52049";

    private static long Number(string itemId) => long.Parse(itemId.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture);

    private static Task<SimulatorResponse> PutItemAsync(SimulatorClient client, string id, object body, string? token, CancellationToken cancellationToken) =>
        client.SendJsonAsync(HttpMethod.Put, $"/items/{id}", body, token, cancellationToken);

    private static async Task<SimulatorResponse> GetItemAsync(SimulatorClient client, string id, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken);
        Assert.True(response.Status == HttpStatusCode.OK, $"GET /items/{id} respondeu {(int)response.Status}: {response.Body}");
        return response;
    }

    /// <summary>400 no formato do ML com uma <c>cause</c> de <paramref name="code"/>; devolve essa cause.</summary>
    private static JsonElement AssertRejected(SimulatorResponse response, string code)
    {
        Assert.True(response.Status == HttpStatusCode.BadRequest, $"Esperava 400 {code}; veio {(int)response.Status}: {response.Body}");
        var json = response.Json;
        Assert.Equal(400, json.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.String, json.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("error").ValueKind);
        var causes = json.GetProperty("cause").EnumerateArray().Where(c => c.GetProperty("code").GetString() == code).ToList();
        Assert.True(causes.Count > 0, $"Esperava cause {code}: {response.Body}");
        return causes[0];
    }

    private static JsonElement? Attribute(JsonElement item, string id) =>
        item.GetProperty("attributes").EnumerateArray().Where(a => a.GetProperty("id").GetString() == id).Select(a => (JsonElement?)a).FirstOrDefault();

    [Fact]
    [Trait("spec", "SPEC-0034:IT-02")]
    public async Task Titulo_e_COLOR_validos_no_3o_ativo_sao_aplicados_no_detalhe_e_no_bulk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];
        var before = DateTimeOffset.Parse(
            (await GetItemAsync(client, id, cancellationToken)).Json.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        // Quando PUT /items/{id} com título e COLOR válidos
        var response = await PutItemAsync(
            client, id, new { title = "Título editado pelo teste", attributes = new[] { new { id = "COLOR", value_id = Preto } } },
            seller.AccessToken, cancellationToken);

        // Então 200, e o detalhe mostra os valores novos com last_updated maior
        Assert.True(response.Status == HttpStatusCode.OK, $"PUT /items/{id} respondeu {(int)response.Status}: {response.Body}");
        var item = (await GetItemAsync(client, id, cancellationToken)).Json;
        Assert.Equal("Título editado pelo teste", item.GetProperty("title").GetString());
        var color = Attribute(item, "COLOR");
        Assert.True(color is not null, $"COLOR ausente depois do PUT: {item}");
        Assert.Equal(Preto, color.Value.GetProperty("value_id").GetString());
        Assert.Equal("Preto", color.Value.GetProperty("value_name").GetString());
        Assert.Equal("Marca Simulada", Attribute(item, "BRAND")!.Value.GetProperty("value_name").GetString());
        var updated = DateTimeOffset.Parse(item.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(updated > before, $"last_updated {updated:o} não é maior que {before:o}.");

        // E o /items/bulk também
        var bulk = await client.GetAsync($"/items/bulk?ids={id}&attributes={SimulatorClient.BulkAttributes}", seller.AccessToken, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, bulk.Status);
        var body = bulk.Json[0].GetProperty("body");
        Assert.Equal("Título editado pelo teste", body.GetProperty("title").GetString());
        Assert.True(
            DateTimeOffset.Parse(body.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture) > before,
            $"last_updated do bulk não mudou: {body}");
    }

    [Theory]
    [Trait("spec", "SPEC-0034:IT-02")]
    [InlineData(0)] // 1º ativo: com vendas
    [InlineData(1)] // 2º ativo: de catálogo
    public async Task Titulo_no_anuncio_com_vendas_ou_de_catalogo_da_field_not_updatable_e_nada_muda(int activeIndex)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[activeIndex];
        var before = await GetItemAsync(client, id, cancellationToken);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        // Quando o PUT traz título (e um COLOR que seria válido)
        var response = await PutItemAsync(
            client, id, new { title = "Título que o ML não aceita", attributes = new[] { new { id = "COLOR", value_id = Preto } } },
            seller.AccessToken, cancellationToken);

        // Então 400 field_not_updatable ("title is not modifiable"), e nada muda (nem o COLOR)
        var cause = AssertRejected(response, "field_not_updatable");
        Assert.Contains("title is not modifiable", cause.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Body, (await GetItemAsync(client, id, cancellationToken)).Body);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:IT-02")]
    [InlineData("ITEM_CONDITION", "2230284", null)] // read_only
    [InlineData("PRODUCT_DATA_SOURCE", null, "manual")] // hidden
    [InlineData("COLOR", "99999999", null)] // value_id inexistente
    public async Task Atributo_nao_editavel_ou_fora_da_lista_da_400_e_nada_muda(string attributeId, string? valueId, string? valueName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];
        var before = await GetItemAsync(client, id, cancellationToken);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        // Quando o PUT traz um título válido e o atributo inválido
        var attribute = valueId is not null
            ? (object)new { id = attributeId, value_id = valueId }
            : new { id = attributeId, value_name = valueName };
        var response = await PutItemAsync(
            client, id, new { title = "Título que seria aceito", attributes = new[] { attribute } }, seller.AccessToken, cancellationToken);

        // Então 400 item.attributes.invalid citando o atributo, e nada muda (nem o título)
        var cause = AssertRejected(response, "item.attributes.invalid");
        Assert.Contains(attributeId, cause.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before.Body, (await GetItemAsync(client, id, cancellationToken)).Body);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-02")]
    public async Task Anuncio_pausado_da_400_de_status_e_nada_muda()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Paused[0];
        var before = await GetItemAsync(client, id, cancellationToken);

        var response = await PutItemAsync(
            client, id, new { attributes = new[] { new { id = "COLOR", value_id = Preto } } }, seller.AccessToken, cancellationToken);

        AssertRejected(response, "item.status.not_active");
        Assert.Equal(before.Body, (await GetItemAsync(client, id, cancellationToken)).Body);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-02")]
    public async Task Sem_token_da_401_e_com_token_de_outro_vendedor_da_403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var other = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];
        var before = await GetItemAsync(client, id, cancellationToken);
        var body = new { title = "Título de quem não é dono" };

        var anonymous = await PutItemAsync(client, id, body, null, cancellationToken);
        var foreign = await PutItemAsync(client, id, body, other.AccessToken, cancellationToken);

        Assert.True(anonymous.Status == HttpStatusCode.Unauthorized, $"Sem token respondeu {(int)anonymous.Status}: {anonymous.Body}");
        Assert.True(foreign.Status == HttpStatusCode.Forbidden, $"Token de outro vendedor respondeu {(int)foreign.Status}: {foreign.Body}");
        Assert.Equal(401, anonymous.Json.GetProperty("status").GetInt32());
        Assert.Equal(403, foreign.Json.GetProperty("status").GetInt32());
        Assert.Equal(before.Body, (await GetItemAsync(client, id, cancellationToken)).Body);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-03")]
    public async Task Descricao_e_criada_por_POST_e_atualizada_por_PUT_e_PUT_sem_descricao_da_404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active.Skip(2).First(item => Number(item) % 2 == 1);
        var path = $"/items/{id}/description";

        // Dado um anúncio sem descrição: GET e PUT dão 404
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path, cancellationToken: cancellationToken)).Status);
        var putWithout = await client.SendJsonAsync(
            HttpMethod.Put, path + "?api_version=2", new { plain_text = "Texto sem descrição prévia" }, seller.AccessToken, cancellationToken);
        Assert.True(putWithout.Status == HttpStatusCode.NotFound, $"PUT sem descrição respondeu {(int)putWithout.Status}: {putWithout.Body}");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path, cancellationToken: cancellationToken)).Status);

        // Quando POST cria
        var created = await client.SendJsonAsync(
            HttpMethod.Post, path, new { plain_text = "Fone criado pelo teste.\nCor: Branco." }, seller.AccessToken, cancellationToken);
        Assert.True(created.Status == HttpStatusCode.Created, $"POST {path} respondeu {(int)created.Status}: {created.Body}");
        var afterCreate = await client.GetAsync(path, cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.OK, afterCreate.Status);
        Assert.Equal("Fone criado pelo teste.\nCor: Branco.", afterCreate.Json.GetProperty("plain_text").GetString());

        // E PUT ?api_version=2 atualiza
        var updated = await client.SendJsonAsync(
            HttpMethod.Put, path + "?api_version=2", new { plain_text = "Fone atualizado, com áudio nítido.\nBateria de 20 h." },
            seller.AccessToken, cancellationToken);
        Assert.True(updated.Status == HttpStatusCode.OK, $"PUT {path} respondeu {(int)updated.Status}: {updated.Body}");
        var afterUpdate = await client.GetAsync(path, cancellationToken: cancellationToken);
        Assert.Equal("Fone atualizado, com áudio nítido.\nBateria de 20 h.", afterUpdate.Json.GetProperty("plain_text").GetString());
    }

    [Theory]
    [Trait("spec", "SPEC-0034:IT-03")]
    [InlineData("<p>Fone de ouvido</p>")]
    [InlineData("Fone de ouvido \U0001F600")]
    public async Task Descricao_com_HTML_ou_emoji_da_400_type_invalid_e_nada_muda(string text)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active.Skip(2).First(item => Number(item) % 2 == 0);
        var path = $"/items/{id}/description";
        var before = await client.GetAsync(path, cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.OK, before.Status);

        var response = await client.SendJsonAsync(HttpMethod.Put, path + "?api_version=2", new { plain_text = text }, seller.AccessToken, cancellationToken);

        AssertRejected(response, "item.description.type.invalid");
        Assert.Equal(
            before.Json.GetProperty("plain_text").GetString(),
            (await client.GetAsync(path, cancellationToken: cancellationToken)).Json.GetProperty("plain_text").GetString());
    }
}
