using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

/// <summary>Par PKCE S256 gerado pelo teste (como a Api gera).</summary>
internal sealed record PkcePair(string Verifier, string Challenge)
{
    public static PkcePair New()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return new PkcePair(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Vendedor criado e conectado, com os IDs ativos e pausados na ordem da massa.</summary>
internal sealed record ConnectedSeller(long Id, string AccessToken, IReadOnlyList<string> Active, IReadOnlyList<string> Paused);

/// <summary>Resposta HTTP já lida: status, cabeçalhos úteis e corpo (JSON quando houver).</summary>
internal sealed record SimulatorResponse(HttpStatusCode Status, string Body, string? ContentType, Uri? Location)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>
/// Cliente HTTP do simulador que age como o navegador e a Api: abre a página de autorização, envia a decisão pelo
/// formulário (campos ocultos da própria página + <c>seller_id</c> + <c>decision</c>), troca/renova tokens
/// (form-urlencoded) e chama a API e os endpoints de controle <c>/__sim/*</c>.
/// </summary>
internal sealed partial class SimulatorClient(HttpClient http) : IDisposable
{
    /// <summary>Valor de <c>decision</c> do botão "Autorizar".</summary>
    public const string Authorize = "authorize";

    /// <summary>Valor de <c>decision</c> do botão "Cancelar".</summary>
    public const string Cancel = "cancel";

    /// <summary>Campos pedidos em <c>attributes</c> do <c>/items/bulk</c> (os do gateway da SPEC-0021).</summary>
    public static readonly string BulkAttributes = string.Join(',',
        "body.id", "body.seller_id", "body.title", "body.price", "body.currency_id", "body.available_quantity",
        "body.sold_quantity", "body.status", "body.permalink", "body.thumbnail", "body.secure_thumbnail",
        "body.listing_type_id", "body.last_updated");

    public HttpClient Http => http;

    public void Dispose() => http.Dispose();

    /// <summary>URL da página de autorização com os parâmetros do contrato (sobrescrevíveis).</summary>
    public static string AuthorizationUrl(
        PkcePair pkce,
        string state,
        string clientId = SimulatorHost.ClientId,
        string redirectUri = SimulatorHost.RedirectUri,
        string method = "S256") =>
        QueryHelpers.AddQueryString("/authorization", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = method,
        });

    public Task<SimulatorResponse> GetAsync(string path, string? accessToken = null, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), accessToken, cancellationToken);

    /// <summary>Abre a página de autorização e envia a decisão; devolve a resposta do POST (302 esperado).</summary>
    public async Task<SimulatorResponse> DecideAsync(
        PkcePair pkce, string state, long sellerId, string decision, string redirectUri = SimulatorHost.RedirectUri,
        CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(AuthorizationUrl(pkce, state, redirectUri: redirectUri), cancellationToken: cancellationToken);
        Assert.True(page.Status == HttpStatusCode.OK, $"GET /authorization respondeu {(int)page.Status}: {page.Body}");
        var fields = HiddenFields(page.Body);
        fields["seller_id"] = sellerId.ToString(CultureInfo.InvariantCulture);
        fields["decision"] = decision;
        return await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/authorization/decision") { Content = new FormUrlEncodedContent(fields) },
            accessToken: null,
            cancellationToken);
    }

    /// <summary>Autoriza o vendedor e devolve o <c>code</c> do redirecionamento.</summary>
    public async Task<string> AuthorizeCodeAsync(
        PkcePair pkce, long sellerId, string redirectUri = SimulatorHost.RedirectUri, CancellationToken cancellationToken = default)
    {
        var state = "estado-" + Guid.NewGuid().ToString("N");
        var response = await DecideAsync(pkce, state, sellerId, Authorize, redirectUri, cancellationToken);
        Assert.True(response.Status == HttpStatusCode.Found, $"POST /authorization/decision respondeu {(int)response.Status}: {response.Body}");
        var query = QueryOf(response.Location!);
        Assert.Equal(state, query["state"]);
        return query["code"];
    }

    public Task<SimulatorResponse> ExchangeAsync(
        string code, string verifier, string redirectUri = SimulatorHost.RedirectUri, string clientId = SimulatorHost.ClientId,
        string clientSecret = SimulatorHost.ClientSecret, CancellationToken cancellationToken = default) =>
        PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }, cancellationToken);

    public Task<SimulatorResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = SimulatorHost.ClientId,
            ["client_secret"] = SimulatorHost.ClientSecret,
            ["refresh_token"] = refreshToken,
        }, cancellationToken);

    /// <summary>Fluxo completo (página, decisão, troca) para o vendedor; devolve a resposta do token (200 exigido).</summary>
    public async Task<JsonElement> ConnectAsync(long sellerId, CancellationToken cancellationToken = default)
    {
        var pkce = PkcePair.New();
        var code = await AuthorizeCodeAsync(pkce, sellerId, cancellationToken: cancellationToken);
        var token = await ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);
        Assert.True(token.Status == HttpStatusCode.OK, $"POST /oauth/token respondeu {(int)token.Status}: {token.Body}");
        return token.Json;
    }

    public Task<SimulatorResponse> PostJsonAsync(string path, object body, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) }, null, cancellationToken);

    public Task<SimulatorResponse> PostAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path), null, cancellationToken);

    /// <summary>Requisição JSON à API do ML (corpo serializado como está: use nomes snake_case) com Bearer opcional.</summary>
    public Task<SimulatorResponse> SendJsonAsync(
        HttpMethod method, string path, object body, string? accessToken, CancellationToken cancellationToken = default) =>
        SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) }, accessToken, cancellationToken);

    /// <summary>Requisição com o JSON cru (ex.: o <c>request</c> de uma fixture) e Bearer opcional.</summary>
    public Task<SimulatorResponse> SendRawJsonAsync(
        HttpMethod method, string path, string json, string? accessToken, CancellationToken cancellationToken = default) =>
        SendAsync(
            new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
            accessToken,
            cancellationToken);

    /// <summary>
    /// Vendedor novo (<c>POST /__sim/sellers</c>, massa padrão 15/6/4) já conectado: id, access token e os IDs ativos e
    /// pausados na ordem da massa (1º ativo com vendas, 2º de catálogo, 3º em diante editáveis — SPEC-0034).
    /// </summary>
    public async Task<ConnectedSeller> CreateConnectedSellerAsync(CancellationToken cancellationToken = default)
    {
        var (id, _) = await CreateSellerAsync(cancellationToken: cancellationToken);
        var token = (await ConnectAsync(id, cancellationToken)).GetProperty("access_token").GetString()!;
        return new ConnectedSeller(
            id,
            token,
            await SearchIdsAsync(id, token, "active", cancellationToken),
            await SearchIdsAsync(id, token, "paused", cancellationToken));
    }

    /// <summary>IDs do vendedor com o status pedido (busca por offset, até 100).</summary>
    public async Task<List<string>> SearchIdsAsync(long sellerId, string accessToken, string status, CancellationToken cancellationToken = default)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"/users/{sellerId}/items/search?status={status}&limit=100");
        var page = await GetAsync(path, accessToken, cancellationToken);
        Assert.True(page.Status == HttpStatusCode.OK, $"GET {path} respondeu {(int)page.Status}: {page.Body}");
        return [.. page.Json.GetProperty("results").EnumerateArray().Select(id => id.GetString()!)];
    }

    /// <summary><c>POST /__sim/sellers</c>; devolve o id criado (201 exigido).</summary>
    public async Task<(long Id, string Nickname)> CreateSellerAsync(object? body = null, CancellationToken cancellationToken = default)
    {
        var response = await PostJsonAsync("/__sim/sellers", body ?? new { }, cancellationToken);
        Assert.True(response.Status == HttpStatusCode.Created, $"POST /__sim/sellers respondeu {(int)response.Status}: {response.Body}");
        var json = response.Json;
        return (json.GetProperty("id").GetInt64(), json.GetProperty("nickname").GetString()!);
    }

    /// <summary>Todos os IDs do vendedor pela busca scan (páginas de 100), seguindo o <c>scroll_id</c> até nulo.</summary>
    public async Task<List<string>> ScanAllAsync(long sellerId, string accessToken, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        string? scrollId = null;
        for (var pages = 1; ; pages++)
        {
            Assert.True(pages <= 200, "O scan passou de 200 páginas.");
            var path = string.Create(CultureInfo.InvariantCulture, $"/users/{sellerId}/items/search?search_type=scan&limit=100")
                + (scrollId is null ? string.Empty : "&scroll_id=" + Uri.EscapeDataString(scrollId));
            var page = await GetAsync(path, accessToken, cancellationToken);
            Assert.True(page.Status == HttpStatusCode.OK, $"GET {path} respondeu {(int)page.Status}: {page.Body}");
            var json = page.Json;
            ids.AddRange(json.GetProperty("results").EnumerateArray().Select(id => id.GetString()!));
            var next = json.GetProperty("scroll_id");
            if (next.ValueKind == JsonValueKind.Null)
            {
                return ids;
            }

            Assert.True(json.GetProperty("results").GetArrayLength() > 0, "Página vazia do scan com scroll_id não nulo.");
            scrollId = next.GetString();
        }
    }

    public static Dictionary<string, string> QueryOf(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);
        return QueryHelpers.ParseQuery(absolute.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>Campos <c>&lt;input type="hidden"&gt;</c> da página (nome → valor, HTML decodificado).</summary>
    public static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in InputTag().Matches(html))
        {
            var tag = input.Value;
            if (!string.Equals(Attribute(tag, "type"), "hidden", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Attribute(tag, "name");
            if (!string.IsNullOrEmpty(name))
            {
                fields[name] = Attribute(tag, "value") ?? string.Empty;
            }
        }

        return fields;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value) : null;
    }

    private Task<SimulatorResponse> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) }, null, cancellationToken);

    private async Task<SimulatorResponse> SendAsync(HttpRequestMessage request, string? accessToken, CancellationToken cancellationToken)
    {
        using (request)
        {
            if (accessToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new SimulatorResponse(response.StatusCode, body, response.Content.Headers.ContentType?.MediaType, response.Headers.Location);
        }
    }

    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();
}
