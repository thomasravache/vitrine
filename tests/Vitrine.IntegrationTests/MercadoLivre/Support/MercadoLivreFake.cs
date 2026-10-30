using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using WireMock;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>Vendedor do ML simulado (<c>GET /users/me</c>). O id passa de Int32 de propósito (épico SPEC-0019).</summary>
internal sealed record MlSeller(long Id, string Nickname, string SiteId = "MLB")
{
    private static long _next = 3_000_000_000;

    public static MlSeller New(string siteId = "MLB")
    {
        var id = Interlocked.Increment(ref _next);
        return new MlSeller(id, $"LOJA_TESTE_{id}", siteId);
    }
}

/// <summary>Tokens fictícios devolvidos pelo ML simulado; distintos por chamada para serem procurados em banco e log.</summary>
internal sealed record MlTokens(string AccessToken, string RefreshToken, int ExpiresIn = 21600)
{
    public static MlTokens New() =>
        new($"APP_USR-access-teste-{Guid.NewGuid():N}", $"TG-refresh-teste-{Guid.NewGuid():N}");
}

/// <summary>
/// Mercado Livre simulado no WireMock (<c>POST /oauth/token</c>, <c>GET /users/me</c> e, para o gateway da SPEC-0021,
/// a busca de IDs e <c>GET /items/bulk</c>), com os formatos de resposta da API real. <see cref="Calls"/> é tudo o que a
/// Api chamou.
/// </summary>
internal sealed class MercadoLivreFake(WireMockServer server)
{
    public const string TokenPath = "/oauth/token";
    public const string UsersMePath = "/users/me";
    public const string BulkPath = "/items/bulk";

    /// <summary>Campos pedidos em <c>attributes</c> do <c>/items/bulk</c> (contrato da SPEC-0021, seção 6).</summary>
    public static readonly string[] BulkAttributes =
    [
        "body.id", "body.seller_id", "body.title", "body.price", "body.currency_id", "body.available_quantity",
        "body.sold_quantity", "body.status", "body.permalink", "body.thumbnail", "body.secure_thumbnail",
        "body.listing_type_id", "body.last_updated",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private int _bulkCalls;

    private int _tokenCalls;

    /// <summary>Requisições recebidas pelo ML simulado.</summary>
    public IReadOnlyList<ILogEntry> Calls => [.. server.LogEntries];

    /// <summary>Requisições recebidas em <c>POST /oauth/token</c>.</summary>
    public IReadOnlyList<ILogEntry> TokenCalls => [.. Calls.Where(entry => entry.RequestMessage?.Path == TokenPath)];

    /// <summary>Apaga respostas configuradas e o histórico de chamadas.</summary>
    public void Reset() => server.Reset();

    /// <summary>Apaga só o histórico de chamadas (as respostas continuam configuradas).</summary>
    public void ForgetCalls() => server.ResetLogEntries();

    /// <summary>Troca de code bem-sucedida e <c>/users/me</c> do vendedor, aceitando só o access token emitido.</summary>
    public void Accepts(MlSeller seller, MlTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(seller);
        TokenResponds(tokens, seller.Id);
        UsersMeResponds(tokens.AccessToken, seller);
    }

    /// <summary>
    /// <c>POST /oauth/token</c> (form-urlencoded, <c>grant_type=authorization_code</c>) responde 200 com os tokens; sem
    /// <paramref name="withRefreshToken"/>, como um app sem o escopo <c>offline_access</c>.
    /// </summary>
    public void TokenResponds(MlTokens tokens, long userId, bool withRefreshToken = true, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var body = new Dictionary<string, object>
        {
            ["access_token"] = tokens.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = tokens.ExpiresIn,
            ["scope"] = withRefreshToken ? "offline_access read write" : "read write",
            ["user_id"] = userId,
        };
        if (withRefreshToken)
        {
            body["refresh_token"] = tokens.RefreshToken;
        }

        var response = Response.Create().WithStatusCode(200).WithBodyAsJson(body);
        server.Given(TokenRequest()).RespondWith(delay is { } wait ? response.WithDelay(wait) : response);
    }

    /// <summary><c>POST /oauth/token</c> responde o erro do ML (<c>{ error, message, status }</c>).</summary>
    public void TokenFails(int status, string error) =>
        server.Given(TokenRequest()).RespondWith(Response.Create()
            .WithStatusCode(status)
            .WithBodyAsJson(new Dictionary<string, object>
            {
                ["error"] = error,
                ["message"] = $"erro simulado: {error}",
                ["status"] = status,
                ["cause"] = Array.Empty<object>(),
            }));

    /// <summary><c>POST /oauth/token</c> só responde depois de <paramref name="delay"/> (timeout do lado da Api).</summary>
    public void TokenDelays(TimeSpan delay, MlTokens tokens, long userId) =>
        server.Given(TokenRequest()).RespondWith(Response.Create()
            .WithStatusCode(200)
            .WithDelay(delay)
            .WithBodyAsJson(new Dictionary<string, object>
            {
                ["access_token"] = tokens.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = tokens.ExpiresIn,
                ["scope"] = "offline_access read write",
                ["user_id"] = userId,
                ["refresh_token"] = tokens.RefreshToken,
            }));

    /// <summary>
    /// <c>GET /users/me</c> com <c>Authorization: Bearer {accessToken}</c> devolve o vendedor, com os dados pessoais que o
    /// ML também devolve (e-mail, nome, documento) e que o Vitrine não pode guardar.
    /// </summary>
    public void UsersMeResponds(string accessToken, MlSeller seller, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(seller);
        server.Given(Request.Create().WithPath(UsersMePath).UsingGet().WithHeader("Authorization", $"Bearer {accessToken}"))
            .RespondWith(Delayed(delay, Response.Create().WithStatusCode(200)).WithBodyAsJson(new Dictionary<string, object>
            {
                ["id"] = seller.Id,
                ["nickname"] = seller.Nickname,
                ["site_id"] = seller.SiteId,
                ["email"] = PersonalEmail(seller),
                ["first_name"] = "Vendedora",
                ["last_name"] = "Ficticia",
                ["identification"] = new Dictionary<string, object> { ["type"] = "CPF", ["number"] = "00000000000" },
                ["country_id"] = seller.SiteId == "MLB" ? "BR" : "AR",
                ["user_type"] = "normal",
            }));
    }

    /// <summary>E-mail que o ML devolve em <c>/users/me</c> e que nunca pode ser guardado.</summary>
    public static string PersonalEmail(MlSeller seller)
    {
        ArgumentNullException.ThrowIfNull(seller);
        return string.Create(CultureInfo.InvariantCulture, $"vendedor.ml.{seller.Id}@example.com");
    }

    /// <summary>Campos do formulário do último <c>POST /oauth/token</c> recebido.</summary>
    public Dictionary<string, string> LastTokenForm()
    {
        var calls = TokenCalls;
        var entry = calls.Count > 0 ? calls[^1] : null;
        Assert.True(entry is not null, "O ML simulado não recebeu POST /oauth/token.");
        var body = entry!.RequestMessage?.Body ?? string.Empty;
        return QueryHelpers.ParseQuery(body).ToDictionary(field => field.Key, field => field.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Chamadas de <c>/items/bulk</c> que chegaram ao ML simulado desde a última configuração do bulk, contadas na
    /// chegada (o log do WireMock só registra a requisição depois da resposta, que pode ainda estar sendo produzida).
    /// </summary>
    public int BulkArrivals => Volatile.Read(ref _bulkCalls);

    /// <summary>Requisições recebidas em <c>GET /items/bulk</c>.</summary>
    public IReadOnlyList<ILogEntry> BulkCalls => [.. Calls.Where(entry => entry.RequestMessage?.Path == BulkPath)];

    /// <summary>Requisições recebidas na busca de IDs do vendedor.</summary>
    public IReadOnlyList<ILogEntry> SearchCalls(long mlUserId) =>
        [.. Calls.Where(entry => entry.RequestMessage?.Path == SearchPath(mlUserId))];

    /// <summary>Parâmetros de query de uma requisição registrada (valores com vírgula ficam inteiros).</summary>
    public static Dictionary<string, string> QueryOf(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var url = new Uri(entry.RequestMessage?.AbsoluteUrl ?? "http://desconhecido/");
        return QueryHelpers.ParseQuery(url.Query).ToDictionary(parameter => parameter.Key, parameter => parameter.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>IDs pedidos (<c>ids</c>) numa chamada de <c>/items/bulk</c>.</summary>
    public static string[] BulkIdsOf(ILogEntry entry) =>
        QueryOf(entry).TryGetValue("ids", out var ids) ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];

    /// <summary>Valor do cabeçalho <c>Authorization</c> de uma requisição registrada.</summary>
    public static string? AuthorizationOf(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var headers = entry.RequestMessage?.Headers;
        return headers is not null && headers.TryGetValue("Authorization", out var values) ? values.ToString() : null;
    }

    /// <summary>
    /// <c>GET /items/bulk</c> responde 200 com um item por ID pedido, no formato <c>[{ id, status_code, body }]</c>, em
    /// ordem inversa à pedida (o gateway reordena). <paramref name="entry"/> monta cada item (padrão: <see cref="MlItem.Found"/>).
    /// Com <paramref name="requiredToken"/>, outro Bearer recebe 401 (token rejeitado pelo ML).
    /// </summary>
    public void BulkResponds(Func<string, object>? entry = null, string? requiredToken = null) =>
        Bulk(_ => null, entry, requiredToken);

    /// <summary>
    /// As <paramref name="times"/> primeiras chamadas de <c>/items/bulk</c> respondem <paramref name="status"/> (com
    /// <c>Retry-After</c> se informado); as seguintes, como <see cref="BulkResponds"/>.
    /// </summary>
    public void BulkFailsFirst(int times, int status, string? retryAfter = null, Func<string, object>? entry = null) =>
        Bulk(call => call <= times ? ErrorResponse(status, retryAfter) : null, entry, requiredToken: null);

    /// <summary>
    /// As <paramref name="times"/> primeiras chamadas de <c>/items/bulk</c> só respondem (200) depois de
    /// <paramref name="delay"/>; as seguintes respondem na hora.
    /// </summary>
    public void BulkDelaysFirst(int times, TimeSpan delay) =>
        Bulk(
            call =>
            {
                if (call <= times)
                {
                    Thread.Sleep(delay);
                }

                return null;
            },
            entry: null,
            requiredToken: null);

    /// <summary>
    /// Toda chamada de <c>/items/bulk</c> responde 401; antes de responder, <paramref name="beforeRespond"/> recebe o
    /// cabeçalho <c>Authorization</c> da requisição (ex.: para outra renovação trocar o token da conta nesse meio-tempo).
    /// </summary>
    public void BulkAlwaysRejects(Action<string?> beforeRespond)
    {
        ArgumentNullException.ThrowIfNull(beforeRespond);
        Interlocked.Exchange(ref _bulkCalls, 0);
        server.Given(Request.Create().WithPath(BulkPath).UsingGet()).RespondWith(Response.Create().WithCallback(request =>
        {
            Interlocked.Increment(ref _bulkCalls);
            beforeRespond(request.Headers is { } headers && headers.TryGetValue("Authorization", out var value) ? value.ToString() : null);
            return ErrorResponse(401, null);
        }));
    }

    /// <summary>Chamadas de <c>POST /oauth/token</c> contadas na chegada desde <see cref="TokenRespondsSlowly"/>.</summary>
    public int TokenArrivals => Volatile.Read(ref _tokenCalls);

    /// <summary>
    /// <c>POST /oauth/token</c> responde 200 com <paramref name="tokens"/> só depois de <paramref name="delay"/>; cada
    /// chamada é contada na chegada (<see cref="TokenArrivals"/>), antes da espera.
    /// </summary>
    public void TokenRespondsSlowly(MlTokens tokens, long userId, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Interlocked.Exchange(ref _tokenCalls, 0);
        server.Given(TokenRequest()).RespondWith(Response.Create().WithCallback(_ =>
        {
            Interlocked.Increment(ref _tokenCalls);
            Thread.Sleep(delay);
            return Message(200, new Dictionary<string, object>
            {
                ["access_token"] = tokens.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = tokens.ExpiresIn,
                ["scope"] = "offline_access read write",
                ["user_id"] = userId,
                ["refresh_token"] = tokens.RefreshToken,
            }, null);
        }));
    }

    /// <summary>Toda chamada de <c>/items/bulk</c> responde <paramref name="status"/>.</summary>
    public void BulkAlwaysFails(int status) => Bulk(_ => ErrorResponse(status, null), entry: null, requiredToken: null);

    /// <summary>
    /// <c>GET /items/bulk</c> responde 200 com o corpo cru <paramref name="json"/> (ex.: a fixture da documentação),
    /// quaisquer que sejam os IDs pedidos.
    /// </summary>
    public void BulkRespondsRaw(string json) =>
        server.Given(Request.Create().WithPath(BulkPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(json));

    /// <summary>
    /// Busca de IDs do vendedor (<c>GET /users/{id}/items/search</c>) sobre <paramref name="ids"/>: por offset devolve
    /// <c>{ results, paging: { total, offset, limit } }</c> (400 com offset ≥ 1000, como o ML); com <c>search_type=scan</c>
    /// devolve páginas de 100 com <c>scroll_id</c> <c>scroll-N</c> encadeado e, no fim, <c>results</c> vazio com o
    /// mesmo <c>scroll_id</c> repetido. Com <paramref name="requiredToken"/>, outro Bearer recebe 401.
    /// </summary>
    public void SearchResponds(long mlUserId, IReadOnlyList<string> ids, string? requiredToken = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        server.Given(Request.Create().WithPath(SearchPath(mlUserId)).UsingGet()).RespondWith(Response.Create().WithCallback(request =>
        {
            if (Rejects(request, requiredToken))
            {
                return Message(401, Error(401, "unauthorized"), null);
            }

            var query = QueryHelpers.ParseQuery(new Uri(request.AbsoluteUrl).Query);
            var limit = int.Parse(query.TryGetValue("limit", out var l) ? l.ToString() : "50", CultureInfo.InvariantCulture);
            if (query.TryGetValue("search_type", out var type) && type == "scan")
            {
                var scroll = query.TryGetValue("scroll_id", out var id) ? id.ToString() : string.Empty;
                var index = scroll.Length == 0 ? 0 : int.Parse(scroll["scroll-".Length..], CultureInfo.InvariantCulture);
                var page = ids.Skip(index * limit).Take(limit).ToArray();
                var next = page.Length == 0 ? scroll : $"scroll-{index + 1}";
                return Message(200, new Dictionary<string, object?>
                {
                    ["seller_id"] = mlUserId.ToString(CultureInfo.InvariantCulture),
                    ["results"] = page,
                    ["scroll_id"] = next,
                    ["paging"] = new Dictionary<string, object> { ["total"] = ids.Count, ["limit"] = limit },
                }, null);
            }

            var offset = int.Parse(query.TryGetValue("offset", out var o) ? o.ToString() : "0", CultureInfo.InvariantCulture);
            if (offset >= 1000)
            {
                return Message(400, Error(400, "bad_request"), null);
            }

            return Message(200, new Dictionary<string, object?>
            {
                ["seller_id"] = mlUserId.ToString(CultureInfo.InvariantCulture),
                ["results"] = ids.Skip(offset).Take(limit).ToArray(),
                ["paging"] = new Dictionary<string, object> { ["total"] = ids.Count, ["offset"] = offset, ["limit"] = limit },
            }, null);
        }));
    }

    public static string SearchPath(long mlUserId) => string.Create(CultureInfo.InvariantCulture, $"/users/{mlUserId}/items/search");

    private void Bulk(Func<int, ResponseMessage?> failure, Func<string, object>? entry, string? requiredToken)
    {
        Interlocked.Exchange(ref _bulkCalls, 0);
        entry ??= id => MlItem.Found(id);
        server.Given(Request.Create().WithPath(BulkPath).UsingGet()).RespondWith(Response.Create().WithCallback(request =>
        {
            var call = Interlocked.Increment(ref _bulkCalls);
            if (failure(call) is { } failed)
            {
                return failed;
            }

            if (Rejects(request, requiredToken))
            {
                return ErrorResponse(401, null);
            }

            var query = QueryHelpers.ParseQuery(new Uri(request.AbsoluteUrl).Query);
            var ids = query.TryGetValue("ids", out var value) ? value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
            return Message(200, ids.Reverse().Select(entry).ToArray(), null);
        }));
    }

    private static bool Rejects(IRequestMessage request, string? requiredToken) =>
        requiredToken is not null
        && !(request.Headers is { } headers
            && headers.TryGetValue("Authorization", out var authorization)
            && authorization.ToString() == $"Bearer {requiredToken}");

    private static ResponseMessage ErrorResponse(int status, string? retryAfter) =>
        Message(status, Error(status, status switch
        {
            401 => "unauthorized",
            429 => "too_many_requests",
            _ => "internal_error",
        }), retryAfter);

    private static Dictionary<string, object> Error(int status, string error) => new()
    {
        ["message"] = $"erro simulado: {error}",
        ["error"] = error,
        ["status"] = status,
        ["cause"] = Array.Empty<object>(),
    };

    private static ResponseMessage Message(int status, object body, string? retryAfter)
    {
        var headers = new Dictionary<string, WireMockList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new("application/json"),
        };
        if (retryAfter is not null)
        {
            headers["Retry-After"] = new(retryAfter);
        }

        return new ResponseMessage
        {
            StatusCode = status,
            Headers = headers,
            BodyData = new BodyData
            {
                DetectedBodyType = BodyType.String,
                BodyAsString = JsonSerializer.Serialize(body, Json),
            },
        };
    }

    private static IResponseBuilder Delayed(TimeSpan? delay, IResponseBuilder response) =>
        delay is { } wait ? response.WithDelay(wait) : response;

    private static IRequestBuilder TokenRequest() => Request.Create().WithPath(TokenPath).UsingPost();
}

/// <summary>Itens do <c>/items/bulk</c> no formato do contrato da SPEC-0021 (<c>{ id, status_code, body }</c>).</summary>
internal static class MlItem
{
    /// <summary>Vendedor dos itens simulados (passa de Int32 de propósito).</summary>
    public const long SellerId = 3_123_456_789;

    /// <summary>Item encontrado (200) com os campos pedidos em <c>attributes</c>; <paramref name="body"/> sobrepõe campos.</summary>
    public static Dictionary<string, object?> Found(string id, Action<Dictionary<string, object?>>? body = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["seller_id"] = SellerId,
            ["title"] = $"Produto de teste {id}",
            ["price"] = 1234.56m,
            ["currency_id"] = "BRL",
            ["available_quantity"] = 7,
            ["sold_quantity"] = 42,
            ["status"] = "active",
            ["permalink"] = $"https://produto.mercadolivre.com.br/{id}-produto-de-teste",
            ["thumbnail"] = $"http://http2.mlstatic.com/D_{id}-I.jpg",
            ["secure_thumbnail"] = $"https://http2.mlstatic.com/D_{id}-I.jpg",
            ["listing_type_id"] = "gold_special",
            ["last_updated"] = "2026-10-29T14:03:12.000Z",
        };
        body?.Invoke(fields);
        return new Dictionary<string, object?> { ["id"] = id, ["status_code"] = 200, ["body"] = fields };
    }

    /// <summary>Item com outro <c>status_code</c> (404 inexistente, 403 sem acesso…).</summary>
    public static Dictionary<string, object?> Failed(string id, int statusCode) => new()
    {
        ["id"] = id,
        ["status_code"] = statusCode,
        ["body"] = new Dictionary<string, object?>
        {
            ["message"] = $"Item with id {id} not found",
            ["error"] = statusCode == 404 ? "not_found" : "forbidden",
            ["status"] = statusCode,
            ["cause"] = Array.Empty<object>(),
        },
    };

    /// <summary>IDs <c>MLB</c> sintéticos e únicos por execução.</summary>
    public static List<string> NewIds(int count)
    {
        var start = Interlocked.Add(ref _next, count) - count;
        return [.. Enumerable.Range(1, count).Select(index => string.Create(CultureInfo.InvariantCulture, $"MLB{start + index}"))];
    }

    private static long _next = 4_000_000_000;
}
