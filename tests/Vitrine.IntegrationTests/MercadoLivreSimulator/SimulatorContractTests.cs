using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0024:CT-01 — as respostas do simulador têm os mesmos campos e tipos JSON das fixtures documentadas do ML
/// (<c>tests/Fixtures/MercadoLivre/</c>, compartilhadas com os CT das SPEC-0021/0023) nos campos usados pelo Vitrine.
/// </summary>
public sealed class SimulatorContractTests
{
    /// <summary>Campos do token usados pela Api (SPEC-0020/0021).</summary>
    private static readonly string[] TokenFields = ["access_token", "token_type", "expires_in", "scope", "user_id", "refresh_token"];

    /// <summary>Campos do erro OAuth usados pela Api (o <c>error</c> decide invalid_grant).</summary>
    private static readonly string[] TokenErrorFields = ["error", "error_description", "status", "cause"];

    /// <summary>Campos de <c>/users/me</c> usados pela Api (SPEC-0020).</summary>
    private static readonly string[] UsersMeFields = ["id", "nickname", "site_id"];

    /// <summary>Campos da busca por offset usados pelo gateway (SPEC-0021).</summary>
    private static readonly string[] SearchFields = ["results", "paging.total", "paging.offset", "paging.limit"];

    /// <summary>Campos da busca scan usados pelo gateway (SPEC-0021).</summary>
    private static readonly string[] ScanFields = ["results", "scroll_id"];

    /// <summary>Campos de cada item do <c>/items/bulk</c> encontrado (SPEC-0021, seção 6).</summary>
    private static readonly string[] BulkFoundFields =
    [
        "id", "status_code", "body.id", "body.seller_id", "body.title", "body.price", "body.currency_id",
        "body.available_quantity", "body.sold_quantity", "body.status", "body.permalink", "body.thumbnail",
        "body.secure_thumbnail", "body.listing_type_id", "body.last_updated",
    ];

    /// <summary>Campos do item não encontrado no <c>/items/bulk</c>.</summary>
    private static readonly string[] BulkMissingFields = ["id", "status_code"];

    /// <summary>Campos da notificação validados pelo webhook (SPEC-0023, seção 6).</summary>
    private static readonly string[] NotificationFields =
        ["_id", "resource", "user_id", "topic", "application_id", "attempts", "sent", "received"];

    [Fact]
    [Trait("spec", "SPEC-0024:CT-01")]
    public async Task Token_users_me_busca_e_bulk_do_simulador_seguem_as_fixtures_documentadas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Token (troca do code) e erro OAuth
        var pkce = PkcePair.New();
        var code = await client.AuthorizeCodeAsync(pkce, SimulatorHost.SellerTeste, cancellationToken: cancellationToken);
        var token = await client.ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.OK, token.Status);
        AssertSameShape("oauth-token.json", token.Json, TokenFields);
        var refreshed = await client.RefreshAsync(token.Json.GetProperty("refresh_token").GetString()!, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        AssertSameShape("oauth-token.json", refreshed.Json, TokenFields);
        var error = await client.ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        AssertSameShape("oauth-token-error.json", error.Json, TokenErrorFields);

        var access = refreshed.Json.GetProperty("access_token").GetString();

        // /users/me
        var me = await client.GetAsync("/users/me", access, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, me.Status);
        AssertSameShape("users-me.json", me.Json, UsersMeFields);

        // Busca por offset e scan
        var search = await client.GetAsync(Path(SimulatorHost.SellerTeste, "limit=50&offset=0"), access, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, search.Status);
        AssertSameShape("items-search.json", search.Json, SearchFields);
        var scan = await client.GetAsync(Path(SimulatorHost.SellerTeste, "search_type=scan&limit=10"), access, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, scan.Status);
        AssertSameShape("items-search-scan.json", scan.Json, ScanFields);

        // /items/bulk com um encontrado e um inexistente
        var found = search.Json.GetProperty("results")[0].GetString();
        var bulk = await client.GetAsync($"/items/bulk?ids={found},MLB1999999999&attributes={SimulatorClient.BulkAttributes}", access, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, bulk.Status);
        var fixture = Fixture("items-bulk.json").GetProperty("response");
        var fixtureFound = fixture.EnumerateArray().First(e => e.GetProperty("status_code").GetInt32() == 200);
        var fixtureMissing = fixture.EnumerateArray().First(e => e.GetProperty("status_code").GetInt32() == 404);
        var entries = bulk.Json.EnumerateArray().ToList();
        AssertSameShape("items-bulk.json[200]", fixtureFound, entries.Single(e => e.GetProperty("status_code").GetInt32() == 200), BulkFoundFields);
        AssertSameShape("items-bulk.json[404]", fixtureMissing, entries.Single(e => e.GetProperty("status_code").GetInt32() == 404), BulkMissingFields);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:CT-01")]
    public async Task Notificacao_do_simulador_segue_a_fixture_documentada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var receiver = WireMockServer.Start();
        receiver.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        await using var host = new SimulatorHost(receiver.Url + "/notificacoes");
        using var client = host.NewClient();

        // Quando o simulador envia uma notificação
        var response = await client.PostJsonAsync("/__sim/notify", new { userId = SimulatorHost.SellerTeste, itemId = "MLB9000000001" }, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.Status);

        // Então o corpo entregue tem os campos e tipos da fixture
        var body = JsonDocument.Parse(Assert.Single(receiver.LogEntries).RequestMessage!.Body!).RootElement;
        AssertSameShape("notification-items.json", Fixture("notification-items.json").GetProperty("notification"), body, NotificationFields);
    }

    private static string Path(long sellerId, string query) =>
        string.Create(CultureInfo.InvariantCulture, $"/users/{sellerId}/items/search?{query}");

    private static JsonElement Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "MercadoLivre", "Fixtures", name))).RootElement.Clone();

    private static void AssertSameShape(string fixture, JsonElement actual, string[] fields) =>
        AssertSameShape(fixture, Fixture(fixture).GetProperty("response"), actual, fields);

    /// <summary>
    /// Cada campo (caminho com ponto) existe na fixture e na resposta com o mesmo tipo JSON (número, texto, lista,
    /// objeto, booleano). Nulo na resposta só é aceito onde a fixture também traz nulo.
    /// </summary>
    private static void AssertSameShape(string fixture, JsonElement expected, JsonElement actual, string[] fields)
    {
        var problems = new List<string>();
        foreach (var field in fields)
        {
            var want = Find(expected, field);
            Assert.True(want is not null, $"A fixture {fixture} não tem o campo '{field}' (fixture incompleta).");
            var got = Find(actual, field);
            if (got is null)
            {
                problems.Add($"'{field}' ausente (fixture: {want.Value.ValueKind})");
            }
            else if (Kind(got.Value) != Kind(want.Value))
            {
                problems.Add($"'{field}' é {got.Value.ValueKind}; a fixture tem {want.Value.ValueKind}");
            }
            else if (want.Value.ValueKind == JsonValueKind.Array && want.Value.GetArrayLength() > 0 && got.Value.GetArrayLength() > 0
                && Kind(want.Value[0]) != Kind(got.Value[0]))
            {
                problems.Add($"'{field}[]' é {got.Value[0].ValueKind}; a fixture tem {want.Value[0].ValueKind}");
            }
        }

        Assert.True(problems.Count == 0, $"Resposta do simulador difere de {fixture}: {string.Join("; ", problems)}\nResposta: {actual}");
    }

    private static JsonElement? Find(JsonElement element, string path)
    {
        var current = element;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static JsonValueKind Kind(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : element.ValueKind;
}
