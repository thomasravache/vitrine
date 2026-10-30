using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings.Support;

/// <summary>Página de <c>GET /api/v1/listings</c>.</summary>
internal sealed record ListingsPage(List<JsonElement> Items, string? NextCursor);

/// <summary>Navegador de um usuário logado contra os endpoints do Listings (contrato da SPEC-0022).</summary>
internal sealed class ListingsApi : IDisposable
{
    public const string ListingsPath = "/api/v1/listings";
    public const string SummaryPath = "/api/v1/listings/summary";
    public const string SyncPath = "/api/v1/listings/sync";

    public ListingsApi(ApiFactory factory)
    {
        Http = new IdentityApi(factory);
    }

    public IdentityApi Http { get; }

    /// <summary>Cadastra um usuário novo (Owner do próprio workspace); a sessão fica neste navegador.</summary>
    public Task<RegisteredAccount> SignUpAsync(CancellationToken cancellationToken) =>
        Http.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);

    /// <summary><c>GET /api/v1/listings?{query}</c> (query já codificada, sem o <c>?</c>).</summary>
    public Task<HttpResponseMessage> ListAsync(string query, CancellationToken cancellationToken) =>
        Http.GetAsync(string.IsNullOrEmpty(query) ? ListingsPath : $"{ListingsPath}?{query}", cancellationToken);

    /// <summary>Lista exigindo 200 com <c>{ items, nextCursor }</c>.</summary>
    public async Task<ListingsPage> ListSuccessfullyAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await ListAsync(query, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, response, cancellationToken);
        var json = await IdentityApi.ReadJsonAsync(response, cancellationToken);
        Assert.True(
            json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array,
            $"GET {ListingsPath} sem o array items: {json}");
        Assert.True(json.TryGetProperty("nextCursor", out var next), $"GET {ListingsPath} sem nextCursor: {json}");
        Assert.True(next.ValueKind is JsonValueKind.String or JsonValueKind.Null, $"nextCursor deve ser string ou null: {json}");
        return new ListingsPage([.. items.EnumerateArray()], next.ValueKind == JsonValueKind.String ? next.GetString() : null);
    }

    /// <summary>Percorre todas as páginas seguindo <c>nextCursor</c>; devolve os itens em ordem e o número de páginas.</summary>
    public async Task<(List<JsonElement> Items, int Pages)> ListAllAsync(string query, CancellationToken cancellationToken)
    {
        var all = new List<JsonElement>();
        var pages = 0;
        string? cursor = null;
        do
        {
            var pageQuery = cursor is null ? query : $"{query}{(query.Length > 0 ? "&" : string.Empty)}cursor={Uri.EscapeDataString(cursor)}";
            var page = await ListSuccessfullyAsync(pageQuery, cancellationToken);
            pages++;
            all.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(pages <= 100, "Paginação não terminou em 100 páginas (nextCursor nunca ficou nulo).");
        }
        while (cursor is not null);

        return (all, pages);
    }

    public Task<HttpResponseMessage> SummaryAsync(CancellationToken cancellationToken) => Http.GetAsync(SummaryPath, cancellationToken);

    public async Task<JsonElement> SummarySuccessfullyAsync(CancellationToken cancellationToken)
    {
        using var response = await SummaryAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, response, cancellationToken);
        return await IdentityApi.ReadJsonAsync(response, cancellationToken);
    }

    /// <summary><c>POST /api/v1/listings/sync</c> com antiforgery; <paramref name="accountId"/> nulo = corpo <c>{}</c>.</summary>
    public Task<HttpResponseMessage> SyncAsync(Guid? accountId, CancellationToken cancellationToken) =>
        Http.PostAsync(SyncPath, accountId is { } id ? new { accountId = id } : new { }, cancellationToken);

    /// <summary>POST sync exigindo 202; devolve <c>queued</c>.</summary>
    public async Task<List<Guid>> SyncAcceptedAsync(Guid? accountId, CancellationToken cancellationToken)
    {
        using var response = await SyncAsync(accountId, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Accepted, response, cancellationToken);
        var json = await IdentityApi.ReadJsonAsync(response, cancellationToken);
        Assert.True(
            json.TryGetProperty("queued", out var queued) && queued.ValueKind == JsonValueKind.Array,
            $"POST {SyncPath} 202 sem o array queued: {json}");
        return [.. queued.EnumerateArray().Select(id => id.GetGuid())];
    }

    public void Dispose() => Http.Dispose();

    /// <summary>Item da listagem com os campos e tipos do contrato.</summary>
    public static void AssertItemShape(JsonElement item)
    {
        string[] fields =
        [
            "itemId", "accountId", "title", "price", "currencyId", "availableQuantity", "soldQuantity", "status", "permalink",
            "thumbnailUrl", "lastUpdated",
        ];
        foreach (var field in fields)
        {
            Assert.True(item.TryGetProperty(field, out _), $"Item da listagem sem {field}: {item}");
        }

        Assert.Equal(JsonValueKind.String, item.GetProperty("itemId").ValueKind);
        Assert.True(item.GetProperty("accountId").TryGetGuid(out _), $"accountId não é UUID: {item}");
        Assert.Equal(JsonValueKind.Number, item.GetProperty("price").ValueKind);
        Assert.True(item.GetProperty("availableQuantity").TryGetInt32(out _), $"availableQuantity não é inteiro: {item}");
        Assert.True(item.GetProperty("soldQuantity").TryGetInt32(out _), $"soldQuantity não é inteiro: {item}");
        Assert.True(item.GetProperty("lastUpdated").TryGetDateTimeOffset(out _), $"lastUpdated não é data-hora: {item}");
    }

    public static string ItemId(JsonElement item) => item.GetProperty("itemId").GetString()!;

    public static DateTimeOffset LastUpdated(JsonElement item) => item.GetProperty("lastUpdated").GetDateTimeOffset();
}
