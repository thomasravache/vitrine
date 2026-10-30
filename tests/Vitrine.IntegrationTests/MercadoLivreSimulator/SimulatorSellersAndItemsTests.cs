using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0024:IT-02 — vendedor criado por <c>POST /__sim/sellers</c>, <c>/users/me</c>, busca por offset e scan,
/// <c>/items/bulk</c>, alteração de anúncio, revogação, expiração do access token e reset.
/// </summary>
public sealed class SimulatorSellersAndItemsTests
{
    private static string Search(long sellerId, string query) =>
        string.Create(CultureInfo.InvariantCulture, $"/users/{sellerId}/items/search?{query}");

    private static string Bulk(IEnumerable<string> ids) =>
        "/items/bulk?ids=" + string.Join(',', ids) + "&attributes=" + SimulatorClient.BulkAttributes;

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Vendedor_criado_tem_id_unico_aparece_na_pagina_e_users_me_o_devolve()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Dado dois vendedores criados por POST /__sim/sellers
        var (id, nickname) = await client.CreateSellerAsync(new { nickname = "LOJA_E2E_UM" }, cancellationToken);
        var (otherId, otherNickname) = await client.CreateSellerAsync(cancellationToken: cancellationToken);

        // Então os ids são únicos, a partir de 3000001000, e o nickname pedido é usado
        Assert.True(id >= 3_000_001_000, $"id {id} < 3000001000");
        Assert.True(otherId >= 3_000_001_000, $"id {otherId} < 3000001000");
        Assert.NotEqual(id, otherId);
        Assert.Equal("LOJA_E2E_UM", nickname);
        Assert.False(string.IsNullOrWhiteSpace(otherNickname));

        // E ambos aparecem na página de autorização (rótulo = nickname)
        var page = await client.GetAsync(SimulatorClient.AuthorizationUrl(PkcePair.New(), "s"), cancellationToken: cancellationToken);
        var html = WebUtility.HtmlDecode(page.Body);
        Assert.Contains(nickname, html, StringComparison.Ordinal);
        Assert.Contains(otherNickname, html, StringComparison.Ordinal);
        Assert.Contains(id.ToString(CultureInfo.InvariantCulture), html, StringComparison.Ordinal);

        // E, conectado, /users/me devolve o vendedor no formato do ML
        var tokens = await client.ConnectAsync(id, cancellationToken);
        Assert.Equal(id, tokens.GetProperty("user_id").GetInt64());
        var me = await client.GetAsync("/users/me", tokens.GetProperty("access_token").GetString(), cancellationToken);
        Assert.True(me.Status == HttpStatusCode.OK, $"/users/me respondeu {(int)me.Status}: {me.Body}");
        var json = me.Json;
        Assert.Equal(id, json.GetProperty("id").GetInt64());
        Assert.Equal(nickname, json.GetProperty("nickname").GetString());
        Assert.Equal("MLB", json.GetProperty("site_id").GetString());
        Assert.Equal("BR", json.GetProperty("country_id").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("user_type").ValueKind);
        Assert.Contains("test_user", json.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Massa_inicial_tem_VENDEDOR_TESTE_com_15_6_4_anuncios_e_VENDEDOR_AR_no_site_MLA()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Dado VENDEDOR_TESTE conectado
        var teste = (await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken)).GetProperty("access_token").GetString();
        var me = (await client.GetAsync("/users/me", teste, cancellationToken)).Json;
        Assert.Equal("VENDEDOR_TESTE", me.GetProperty("nickname").GetString());
        Assert.Equal("MLB", me.GetProperty("site_id").GetString());

        // Então a busca mostra 25 anúncios: 15 ativos, 6 pausados e 4 encerrados, com IDs MLB9000000001…
        Assert.Equal(25, await TotalAsync(client, SimulatorHost.SellerTeste, teste!, "limit=50&offset=0", cancellationToken));
        Assert.Equal(15, await TotalAsync(client, SimulatorHost.SellerTeste, teste!, "status=active", cancellationToken));
        Assert.Equal(6, await TotalAsync(client, SimulatorHost.SellerTeste, teste!, "status=paused", cancellationToken));
        Assert.Equal(4, await TotalAsync(client, SimulatorHost.SellerTeste, teste!, "status=closed", cancellationToken));
        var ids = (await client.GetAsync(Search(SimulatorHost.SellerTeste, "limit=50"), teste, cancellationToken)).Json
            .GetProperty("results").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.Contains("MLB9000000001", ids);

        // E VENDEDOR_AR é do site MLA
        var ar = (await client.ConnectAsync(SimulatorHost.SellerAr, cancellationToken)).GetProperty("access_token").GetString();
        var meAr = (await client.GetAsync("/users/me", ar, cancellationToken)).Json;
        Assert.Equal("VENDEDOR_AR", meAr.GetProperty("nickname").GetString());
        Assert.Equal("MLA", meAr.GetProperty("site_id").GetString());
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Vendedor_criado_com_massa_pedida_e_site_MLA()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Dado um vendedor criado com siteId MLA e massa 3/2/1
        var (id, _) = await client.CreateSellerAsync(new { siteId = "MLA", active = 3, paused = 2, closed = 1 }, cancellationToken);
        var token = (await client.ConnectAsync(id, cancellationToken)).GetProperty("access_token").GetString()!;

        // Então /users/me mostra MLA e a busca mostra a massa pedida
        Assert.Equal("MLA", (await client.GetAsync("/users/me", token, cancellationToken)).Json.GetProperty("site_id").GetString());
        Assert.Equal(6, await TotalAsync(client, id, token, "offset=0", cancellationToken));
        Assert.Equal(3, await TotalAsync(client, id, token, "status=active", cancellationToken));
        Assert.Equal(2, await TotalAsync(client, id, token, "status=paused", cancellationToken));
        Assert.Equal(1, await TotalAsync(client, id, token, "status=closed", cancellationToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Busca_por_offset_e_scan_devolve_os_ids_da_massa_e_offset_acima_de_1000_responde_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Dado um vendedor criado (padrão 15/6/4) com mais 1100 anúncios ativos sintéticos
        var (id, _) = await client.CreateSellerAsync(cancellationToken: cancellationToken);
        var added = await client.PostJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{id}/items"), new { count = 1100 }, cancellationToken);
        Assert.True((int)added.Status is >= 200 and < 300, $"POST /__sim/sellers/{id}/items respondeu {(int)added.Status}: {added.Body}");
        var token = (await client.ConnectAsync(id, cancellationToken)).GetProperty("access_token").GetString()!;

        // Então a busca por offset respeita offset+limit <= 1000 e o paging
        var page = await client.GetAsync(Search(id, "offset=20&limit=10"), token, cancellationToken);
        Assert.True(page.Status == HttpStatusCode.OK, $"busca respondeu {(int)page.Status}: {page.Body}");
        var json = page.Json;
        Assert.Equal(10, json.GetProperty("results").GetArrayLength());
        Assert.Equal(1125, json.GetProperty("paging").GetProperty("total").GetInt32());
        Assert.Equal(20, json.GetProperty("paging").GetProperty("offset").GetInt32());
        Assert.Equal(10, json.GetProperty("paging").GetProperty("limit").GetInt32());
        Assert.Equal(50, (await client.GetAsync(Search(id, "offset=0"), token, cancellationToken)).Json.GetProperty("paging").GetProperty("limit").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Search(id, "offset=900&limit=100"), token, cancellationToken)).Status);
        Assert.Equal(1115, await TotalAsync(client, id, token, "status=active", cancellationToken));

        // E offset+limit > 1000 sem scan responde 400
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Search(id, "offset=950&limit=100"), token, cancellationToken)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Search(id, "offset=1000&limit=50"), token, cancellationToken)).Status);

        // E o scan devolve todos os 1125 IDs, cada um uma vez, terminando com scroll_id null
        var all = await client.ScanAllAsync(id, token, cancellationToken);
        Assert.Equal(1125, all.Count);
        Assert.Equal(1125, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, itemId => Assert.StartsWith("MLB", itemId, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Busca_sem_token_responde_401_e_com_token_de_outro_vendedor_403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var (id, _) = await client.CreateSellerAsync(cancellationToken: cancellationToken);
        var otherToken = (await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken)).GetProperty("access_token").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Search(id, "offset=0"), cancellationToken: cancellationToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Search(id, "offset=0"), otherToken, cancellationToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/users/me", "APP_USR-desconhecido", cancellationToken)).Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Bulk_com_20_ids_devolve_19_200_e_1_404_e_com_21_ids_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var (id, _) = await client.CreateSellerAsync(cancellationToken: cancellationToken);
        var token = (await client.ConnectAsync(id, cancellationToken)).GetProperty("access_token").GetString()!;
        var ids = (await client.GetAsync(Search(id, "limit=19"), token, cancellationToken)).Json
            .GetProperty("results").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.Equal(19, ids.Count);
        const string missing = "MLB1999999999";

        // Quando /items/bulk recebe 20 IDs (1 inexistente)
        var bulk = await client.GetAsync(Bulk([.. ids, missing]), token, cancellationToken);

        // Então devolve 20 itens: 19 com status_code 200 (body do vendedor) e 1 com 404
        Assert.True(bulk.Status == HttpStatusCode.OK, $"/items/bulk respondeu {(int)bulk.Status}: {bulk.Body}");
        var entries = bulk.Json.EnumerateArray().ToList();
        Assert.Equal(20, entries.Count);
        var found = entries.Where(e => e.GetProperty("status_code").GetInt32() == 200).ToList();
        Assert.Equal(19, found.Count);
        Assert.Equal(ids.Order(StringComparer.Ordinal), found.Select(e => e.GetProperty("id").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(found, e =>
        {
            Assert.Equal(e.GetProperty("id").GetString(), e.GetProperty("body").GetProperty("id").GetString());
            Assert.Equal(id, e.GetProperty("body").GetProperty("seller_id").GetInt64());
        });
        var notFound = Assert.Single(entries, e => e.GetProperty("status_code").GetInt32() == 404);
        Assert.Equal(missing, notFound.GetProperty("id").GetString());

        // E com 21 IDs responde 400; sem token, 401
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Bulk([.. ids, missing, "MLB1999999998"]), token, cancellationToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Bulk(ids), cancellationToken: cancellationToken)).Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Alterar_anuncio_pelo_controle_reflete_no_bulk_e_atualiza_last_updated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var (id, _) = await client.CreateSellerAsync(cancellationToken: cancellationToken);
        var token = (await client.ConnectAsync(id, cancellationToken)).GetProperty("access_token").GetString()!;
        var itemId = (await client.GetAsync(Search(id, "status=active&limit=1"), token, cancellationToken)).Json
            .GetProperty("results")[0].GetString()!;
        var before = (await client.GetAsync(Bulk([itemId]), token, cancellationToken)).Json[0].GetProperty("body");

        // Quando o teste altera o anúncio por /__sim/sellers/{id}/items/{itemId}
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var changed = await client.PostJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{id}/items/{itemId}"),
            new { title = "Título alterado pelo teste", price = 99.9m, availableQuantity = 3, soldQuantity = 8, status = "paused" },
            cancellationToken);
        Assert.True((int)changed.Status is >= 200 and < 300, $"alteração respondeu {(int)changed.Status}: {changed.Body}");

        // Então o bulk devolve os campos novos e um last_updated posterior
        var after = (await client.GetAsync(Bulk([itemId]), token, cancellationToken)).Json[0].GetProperty("body");
        Assert.Equal("Título alterado pelo teste", after.GetProperty("title").GetString());
        Assert.Equal(99.9m, after.GetProperty("price").GetDecimal());
        Assert.Equal(3, after.GetProperty("available_quantity").GetInt32());
        Assert.Equal(8, after.GetProperty("sold_quantity").GetInt32());
        Assert.Equal("paused", after.GetProperty("status").GetString());
        Assert.True(
            DateTimeOffset.Parse(after.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture)
            > DateTimeOffset.Parse(before.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture),
            "last_updated não avançou depois da alteração.");
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Expire_access_token_faz_a_API_responder_401_e_a_renovacao_continua_valendo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var (id, _) = await client.CreateSellerAsync(cancellationToken: cancellationToken);
        var tokens = await client.ConnectAsync(id, cancellationToken);

        // Quando o access token atual é expirado à força
        var expired = await client.PostAsync(string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{id}/expire-access-token"), cancellationToken);
        Assert.True((int)expired.Status is >= 200 and < 300, $"expire-access-token respondeu {(int)expired.Status}: {expired.Body}");

        // Então a API responde 401 com ele
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/users/me", tokens.GetProperty("access_token").GetString(), cancellationToken)).Status);

        // E a renovação devolve um access novo que funciona
        var renewed = await client.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, renewed.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/users/me", renewed.Json.GetProperty("access_token").GetString(), cancellationToken)).Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Revoke_faz_a_renovacao_responder_invalid_grant_e_a_API_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var (id, _) = await client.CreateSellerAsync(cancellationToken: cancellationToken);
        var tokens = await client.ConnectAsync(id, cancellationToken);
        var other = await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken);

        // Quando o vendedor é revogado
        var revoked = await client.PostAsync(string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{id}/revoke"), cancellationToken);
        Assert.True((int)revoked.Status is >= 200 and < 300, $"revoke respondeu {(int)revoked.Status}: {revoked.Body}");

        // Então o próximo refresh dá invalid_grant e o access token dá 401
        SimulatorOAuthTests.AssertOAuthError(
            await client.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!, cancellationToken), HttpStatusCode.BadRequest, "invalid_grant");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/users/me", tokens.GetProperty("access_token").GetString(), cancellationToken)).Status);

        // E os tokens de outro vendedor continuam valendo
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/users/me", other.GetProperty("access_token").GetString(), cancellationToken)).Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-02")]
    public async Task Reset_volta_a_massa_inicial_e_invalida_todos_os_tokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var (_, nickname) = await client.CreateSellerAsync(new { nickname = "LOJA_ANTES_DO_RESET" }, cancellationToken);
        var tokens = await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken);

        // Quando o teste chama /__sim/reset
        var reset = await client.PostAsync("/__sim/reset", cancellationToken);
        Assert.True((int)reset.Status is >= 200 and < 300, $"reset respondeu {(int)reset.Status}: {reset.Body}");

        // Então o vendedor criado some da página, a massa inicial continua e os tokens anteriores não valem mais
        var html = WebUtility.HtmlDecode((await client.GetAsync(SimulatorClient.AuthorizationUrl(PkcePair.New(), "s"), cancellationToken: cancellationToken)).Body);
        Assert.DoesNotContain(nickname, html, StringComparison.Ordinal);
        Assert.Contains("VENDEDOR_TESTE", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/users/me", tokens.GetProperty("access_token").GetString(), cancellationToken)).Status);
        SimulatorOAuthTests.AssertOAuthError(
            await client.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!, cancellationToken), HttpStatusCode.BadRequest, "invalid_grant");
    }

    private static async Task<int> TotalAsync(SimulatorClient client, long sellerId, string token, string query, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(Search(sellerId, query), token, cancellationToken);
        Assert.True(response.Status == HttpStatusCode.OK, $"busca '{query}' respondeu {(int)response.Status}: {response.Body}");
        return response.Json.GetProperty("paging").GetProperty("total").GetInt32();
    }
}
