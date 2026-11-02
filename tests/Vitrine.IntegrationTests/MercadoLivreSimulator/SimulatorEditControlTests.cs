using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0034:IT-05 — controles <c>/__sim/*</c> para preparar cenários de edição: vendas, catálogo, descrição e atributos
/// do anúncio; falha forçada na próxima escrita (<c>fail-next-write</c>); registro de escritas; reset.
/// </summary>
public sealed class SimulatorEditControlTests
{
    /// <summary><c>value_id</c> de "Preto" em <c>COLOR</c> (o mesmo da fixture <c>category-attributes.json</c>).</summary>
    private const string Preto = "52049";

    private static long Number(string itemId) => long.Parse(itemId.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Control(long sellerId, string itemId, string suffix = "") =>
        string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{sellerId}/items/{itemId}{suffix}");

    private static async Task FailNextWriteAsync(SimulatorClient client, long sellerId, string itemId, object body, CancellationToken cancellationToken)
    {
        var response = await client.PostJsonAsync(Control(sellerId, itemId, "/fail-next-write"), body, cancellationToken);
        Assert.True(response.Status == HttpStatusCode.NoContent, $"fail-next-write respondeu {(int)response.Status}: {response.Body}");
    }

    private static async Task<List<JsonElement>> WritesAsync(SimulatorClient client, long sellerId, string itemId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(Control(sellerId, itemId, "/writes"), cancellationToken: cancellationToken);
        Assert.True(response.Status == HttpStatusCode.OK, $"GET .../writes respondeu {(int)response.Status}: {response.Body}");
        return [.. response.Json.EnumerateArray()];
    }

    private static Task<SimulatorResponse> PutTitleAsync(SimulatorClient client, string itemId, string title, string token, CancellationToken cancellationToken) =>
        client.SendJsonAsync(HttpMethod.Put, $"/items/{itemId}", new { title }, token, cancellationToken);

    private static Task<SimulatorResponse> PutDescriptionAsync(SimulatorClient client, string itemId, string text, string token, CancellationToken cancellationToken) =>
        client.SendJsonAsync(HttpMethod.Put, $"/items/{itemId}/description?api_version=2", new { plain_text = text }, token, cancellationToken);

    [Fact]
    [Trait("spec", "SPEC-0034:IT-05")]
    public async Task Controle_do_anuncio_altera_vendas_catalogo_descricao_e_atributos_e_descricao_vazia_apaga()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];

        // Quando o controle altera vendas, catálogo, descrição e atributos
        var changed = await client.PostJsonAsync(
            Control(seller.Id, id),
            new
            {
                soldQuantity = 3,
                catalogListing = true,
                description = "Descrição preparada pelo controle.",
                attributes = new[] { new { id = "COLOR", value_id = Preto } },
            },
            cancellationToken);
        Assert.True(changed.Status == HttpStatusCode.OK, $"POST {Control(seller.Id, id)} respondeu {(int)changed.Status}: {changed.Body}");

        // Então o anúncio reflete tudo (a lista de atributos é substituída)
        var item = (await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken)).Json;
        Assert.Equal(3, item.GetProperty("sold_quantity").GetInt32());
        Assert.Equal(JsonValueKind.True, item.GetProperty("catalog_listing").ValueKind);
        var attribute = Assert.Single(item.GetProperty("attributes").EnumerateArray());
        Assert.Equal("COLOR", attribute.GetProperty("id").GetString());
        Assert.Equal(Preto, attribute.GetProperty("value_id").GetString());
        var description = await client.GetAsync($"/items/{id}/description", cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.OK, description.Status);
        Assert.Equal("Descrição preparada pelo controle.", description.Json.GetProperty("plain_text").GetString());

        // E description "" apaga a descrição
        var cleared = await client.PostJsonAsync(Control(seller.Id, id), new { description = string.Empty }, cancellationToken);
        Assert.True(cleared.Status == HttpStatusCode.OK, $"description \"\" respondeu {(int)cleared.Status}: {cleared.Body}");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/items/{id}/description", cancellationToken: cancellationToken)).Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-05")]
    public async Task Fail_next_write_409_no_item_responde_409_sem_mudar_nada_e_a_seguinte_e_normal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];
        var before = await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken);

        // Dado fail-next-write { target: "item", status: 409 }
        await FailNextWriteAsync(client, seller.Id, id, new { target = "item", status = 409 }, cancellationToken);

        // Quando o próximo PUT chega: 409 no formato do ML, sem mudar nada
        var forced = await PutTitleAsync(client, id, "Título na escrita forçada", seller.AccessToken, cancellationToken);
        Assert.True(forced.Status == HttpStatusCode.Conflict, $"PUT forçado respondeu {(int)forced.Status}: {forced.Body}");
        Assert.Equal(409, forced.Json.GetProperty("status").GetInt32());
        Assert.Equal(before.Body, (await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken)).Body);

        // E o seguinte é normal
        var next = await PutTitleAsync(client, id, "Título depois da falha", seller.AccessToken, cancellationToken);
        Assert.True(next.Status == HttpStatusCode.OK, $"PUT seguinte respondeu {(int)next.Status}: {next.Body}");
        Assert.Equal("Título depois da falha", (await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken)).Json.GetProperty("title").GetString());
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-05")]
    public async Task Fail_next_write_500_e_400_com_causeCode_na_descricao_respondem_assim_sem_mudar_nada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active.Skip(2).First(item => Number(item) % 2 == 0);
        var descriptionPath = $"/items/{id}/description";
        var original = (await client.GetAsync(descriptionPath, cancellationToken: cancellationToken)).Json.GetProperty("plain_text").GetString();

        // 500 no alvo description
        await FailNextWriteAsync(client, seller.Id, id, new { target = "description", status = 500 }, cancellationToken);
        var failed = await PutDescriptionAsync(client, id, "Descrição na falha 500.", seller.AccessToken, cancellationToken);
        Assert.True(failed.Status == HttpStatusCode.InternalServerError, $"PUT forçado (500) respondeu {(int)failed.Status}: {failed.Body}");
        Assert.Equal(original, (await client.GetAsync(descriptionPath, cancellationToken: cancellationToken)).Json.GetProperty("plain_text").GetString());

        // 400 com causeCode no alvo description
        await FailNextWriteAsync(
            client, seller.Id, id, new { target = "description", status = 400, causeCode = "item.description.type.invalid" }, cancellationToken);
        var rejected = await PutDescriptionAsync(client, id, "Descrição válida, mas a falha é forçada.", seller.AccessToken, cancellationToken);
        Assert.True(rejected.Status == HttpStatusCode.BadRequest, $"PUT forçado (400) respondeu {(int)rejected.Status}: {rejected.Body}");
        Assert.Contains(
            rejected.Json.GetProperty("cause").EnumerateArray(),
            cause => cause.GetProperty("code").GetString() == "item.description.type.invalid");
        Assert.Equal(original, (await client.GetAsync(descriptionPath, cancellationToken: cancellationToken)).Json.GetProperty("plain_text").GetString());

        // A falha no alvo description não afeta o item, e a escrita seguinte da descrição é normal
        var item = await PutTitleAsync(client, id, "Título sem falha forçada", seller.AccessToken, cancellationToken);
        Assert.True(item.Status == HttpStatusCode.OK, $"PUT do item respondeu {(int)item.Status}: {item.Body}");
        var next = await PutDescriptionAsync(client, id, "Descrição depois das falhas.", seller.AccessToken, cancellationToken);
        Assert.True(next.Status == HttpStatusCode.OK, $"PUT seguinte respondeu {(int)next.Status}: {next.Body}");
        Assert.Equal(
            "Descrição depois das falhas.",
            (await client.GetAsync(descriptionPath, cancellationToken: cancellationToken)).Json.GetProperty("plain_text").GetString());
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-05")]
    public async Task Registro_de_escritas_lista_metodo_caminho_corpo_e_status_na_ordem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active.Skip(2).First(item => Number(item) % 2 == 1);

        // Dado três escritas: PUT forçado a 409, PUT aceito e POST da descrição
        await FailNextWriteAsync(client, seller.Id, id, new { target = "item", status = 409 }, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, (await PutTitleAsync(client, id, "Primeiro titulo", seller.AccessToken, cancellationToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PutTitleAsync(client, id, "Segundo titulo", seller.AccessToken, cancellationToken)).Status);
        var created = await client.SendJsonAsync(
            HttpMethod.Post, $"/items/{id}/description", new { plain_text = "Descricao registrada." }, seller.AccessToken, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.Status);

        // Quando GET .../writes
        var writes = await WritesAsync(client, seller.Id, id, cancellationToken);

        // Então lista as três, na ordem, com método, caminho, corpo, status e data
        Assert.Equal(3, writes.Count);
        Assert.Equal(["PUT", "PUT", "POST"], writes.Select(w => w.GetProperty("method").GetString()));
        Assert.Equal([409, 200, 201], writes.Select(w => w.GetProperty("status").GetInt32()));
        Assert.StartsWith($"/items/{id}", writes[0].GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.StartsWith($"/items/{id}/description", writes[2].GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Contains("Primeiro titulo", writes[0].GetProperty("body").ToString(), StringComparison.Ordinal);
        Assert.Contains("Segundo titulo", writes[1].GetProperty("body").ToString(), StringComparison.Ordinal);
        Assert.Contains("Descricao registrada.", writes[2].GetProperty("body").ToString(), StringComparison.Ordinal);
        Assert.All(writes, w => Assert.True(
            DateTimeOffset.TryParse(w.GetProperty("at").GetString(), CultureInfo.InvariantCulture, out _), $"'at' inválido: {w}"));
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-05")]
    public async Task Reset_limpa_as_falhas_forcadas_e_o_registro_de_escritas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var token = (await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken)).GetProperty("access_token").GetString()!;
        var id = (await client.SearchIdsAsync(SimulatorHost.SellerTeste, token, "active", cancellationToken))[2];

        // Dado uma escrita registrada e uma falha forçada pendente
        Assert.Equal(HttpStatusCode.OK, (await PutTitleAsync(client, id, "Título antes do reset", token, cancellationToken)).Status);
        Assert.Single(await WritesAsync(client, SimulatorHost.SellerTeste, id, cancellationToken));
        await FailNextWriteAsync(client, SimulatorHost.SellerTeste, id, new { target = "item", status = 409 }, cancellationToken);

        // Quando POST /__sim/reset
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/__sim/reset", cancellationToken)).Status);

        // Então o registro está vazio e a próxima escrita não falha
        Assert.Empty(await WritesAsync(client, SimulatorHost.SellerTeste, id, cancellationToken));
        var newToken = (await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken)).GetProperty("access_token").GetString()!;
        var response = await PutTitleAsync(client, id, "Título depois do reset", newToken, cancellationToken);
        Assert.True(response.Status == HttpStatusCode.OK, $"PUT depois do reset respondeu {(int)response.Status}: {response.Body}");
    }
}
