using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>
/// Endpoints que imitam o subconjunto documentado do ML usado pelo Vitrine (SPEC-0024, seção 6): página de autorização,
/// <c>/oauth/token</c>, <c>/users/me</c>, busca de anúncios e <c>/items/bulk</c>.
/// </summary>
internal static class MlEndpoints
{
    private const string Scope = "offline_access read write";
    private const int DefaultSearchLimit = 50;
    private const int MaxSearchLimit = 100;
    private const int MaxBulkIds = 20;

    public static void MapMercadoLivre(this IEndpointRouteBuilder app)
    {
        app.MapGet("/authorization", Authorization);
        app.MapPost("/authorization/decision", DecisionAsync);
        app.MapPost("/oauth/token", TokenAsync);
        app.MapGet("/users/me", UsersMe);
        app.MapGet("/users/{sellerId:long}/items/search", SearchItems);
        app.MapGet("/items/bulk", Bulk);
    }

    private static IResult Authorization(HttpRequest request, SimulatorState state, IOptions<SimulatorOptions> options)
    {
        var parameters = AuthorizationPage.ForwardedParameters.ToDictionary(
            name => name, name => (string?)request.Query[name].ToString(), StringComparer.Ordinal);
        if (RejectedParameter(parameters, options.Value) is { } rejected)
        {
            return Html(AuthorizationPage.RenderError(rejected.Parameter, rejected.Reason), StatusCodes.Status400BadRequest);
        }

        return Html(AuthorizationPage.Render(state.Sellers(), parameters), StatusCodes.Status200OK);
    }

    private static async Task<IResult> DecisionAsync(HttpRequest request, SimulatorState state, IOptions<SimulatorOptions> options)
    {
        if (!request.HasFormContentType)
        {
            return Html(AuthorizationPage.RenderError("decision", "O formulário precisa ser enviado como application/x-www-form-urlencoded."), StatusCodes.Status400BadRequest);
        }

        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var parameters = AuthorizationPage.ForwardedParameters.ToDictionary(
            name => name, name => (string?)form[name].ToString(), StringComparer.Ordinal);

        // Os parâmetros voltam do navegador: valida de novo antes de redirecionar (nunca para um redirect_uri não aceito).
        if (RejectedParameter(parameters, options.Value) is { } rejected)
        {
            return Html(AuthorizationPage.RenderError(rejected.Parameter, rejected.Reason), StatusCodes.Status400BadRequest);
        }

        var redirectUri = parameters["redirect_uri"]!;
        var returnedState = parameters["state"];
        var query = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (form["decision"].ToString())
        {
            case "cancel":
                query["error"] = "access_denied";
                query["error_description"] = "O vendedor cancelou a autorização.";
                break;
            case "authorize":
                if (!long.TryParse(form["seller_id"], NumberStyles.None, CultureInfo.InvariantCulture, out var sellerId)
                    || state.FindSeller(sellerId) is null)
                {
                    return Html(AuthorizationPage.RenderError("seller_id", "Escolha um dos vendedores da lista."), StatusCodes.Status400BadRequest);
                }

                query["code"] = state.IssueCode(parameters["client_id"]!, redirectUri, parameters["code_challenge"]!, sellerId);
                break;
            default:
                return Html(AuthorizationPage.RenderError("decision", "Use os botões Autorizar ou Cancelar."), StatusCodes.Status400BadRequest);
        }

        if (!string.IsNullOrEmpty(returnedState))
        {
            query["state"] = returnedState;
        }

        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, query));
    }

    private static async Task<IResult> TokenAsync(
        HttpRequest request, SimulatorState state, TokenIssuer issuer, IOptions<SimulatorOptions> options)
    {
        if (!request.HasFormContentType)
        {
            return OAuthFailure(StatusCodes.Status400BadRequest, "invalid_request", "Use application/x-www-form-urlencoded.");
        }

        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var settings = options.Value;
        if (!string.Equals(form["client_id"], settings.ClientId, StringComparison.Ordinal)
            || !string.Equals(form["client_secret"], settings.ClientSecret, StringComparison.Ordinal))
        {
            return OAuthFailure(StatusCodes.Status401Unauthorized, "invalid_client", "Invalid client_id or client_secret.");
        }

        IssuedTokens? tokens;
        switch (form["grant_type"].ToString())
        {
            case "authorization_code":
                var code = state.TakeCode(form["code"].ToString());
                tokens = code is not null
                    && code.ClientId == settings.ClientId
                    && string.Equals(code.RedirectUri, form["redirect_uri"], StringComparison.Ordinal)
                    && Pkce.Verify(form["code_verifier"].ToString(), code.CodeChallenge)
                        ? issuer.Issue(code.SellerId)
                        : null;
                break;
            case "refresh_token":
                tokens = issuer.Refresh(form["refresh_token"].ToString());
                break;
            default:
                return OAuthFailure(StatusCodes.Status400BadRequest, "unsupported_grant_type", "Use authorization_code or refresh_token.");
        }

        return tokens is null
            ? OAuthFailure(
                StatusCodes.Status400BadRequest,
                "invalid_grant",
                "Error validating grant. Your authorization code or refresh token may be expired or it was already used")
            : Results.Json(
                new TokenResponse(tokens.AccessToken, "Bearer", tokens.ExpiresIn, Scope, tokens.SellerId, tokens.RefreshToken),
                MlJson.Options);
    }

    private static IResult UsersMe(HttpRequest request, SimulatorState state, TokenIssuer issuer)
    {
        if (Caller(request, issuer) is not { } sellerId || state.FindSeller(sellerId) is not { } seller)
        {
            return Unauthorized();
        }

        return Results.Json(
            new UserResponse(
                seller.Id,
                seller.Nickname,
                MlJson.Date(seller.RegisteredAt),
                seller.Site.CountryId,
                "normal",
                ["normal", "test_user"],
                seller.Site.Id,
                $"http://perfil.{seller.Site.Domain}/{Uri.EscapeDataString(seller.Nickname)}"),
            MlJson.Options);
    }

    private static IResult SearchItems(long sellerId, HttpRequest request, SimulatorState state, TokenIssuer issuer, ItemSearch search)
    {
        if (Caller(request, issuer) is not { } caller)
        {
            return Unauthorized();
        }

        if (caller != sellerId)
        {
            return ApiFailure(StatusCodes.Status403Forbidden, "forbidden", "caller.id does not match the user.");
        }

        var query = request.Query;
        var status = query["status"].ToString();
        if (status.Length > 0 && !SimulatorState.Statuses.Contains(status))
        {
            return ApiFailure(StatusCodes.Status400BadRequest, "bad_request", "status must be active, paused or closed.");
        }

        if (!TryInt(query["limit"], DefaultSearchLimit, out var limit) || limit is < 1 or > MaxSearchLimit)
        {
            return ApiFailure(StatusCodes.Status400BadRequest, "bad_request", "limit must be between 1 and 100.");
        }

        var ids = state.ItemIds(sellerId, status.Length > 0 ? status : null);
        var seller = sellerId.ToString(CultureInfo.InvariantCulture);
        if (query["search_type"].ToString() == "scan")
        {
            var scrollId = query["scroll_id"].ToString();
            var page = search.Scan(ids, scrollId.Length > 0 ? scrollId : null, limit);
            return Results.Json(new ScanResponse(seller, null, new ScanPaging(limit, ids.Count), page.Results, page.ScrollId), MlJson.Options);
        }

        if (!TryInt(query["offset"], 0, out var offset) || offset < 0)
        {
            return ApiFailure(StatusCodes.Status400BadRequest, "bad_request", "offset must be a non-negative number.");
        }

        if (search.ByOffset(ids, offset, limit) is not { } result)
        {
            return ApiFailure(
                StatusCodes.Status400BadRequest,
                "bad_request",
                "offset + limit must be lower or equal than 1000. Use search_type=scan for bigger catalogs.");
        }

        return Results.Json(
            new SearchResponse(seller, null, new OffsetPaging(result.Limit, result.Offset, result.Total), result.Results),
            MlJson.Options);
    }

    private static IResult Bulk(HttpRequest request, SimulatorState state, TokenIssuer issuer)
    {
        if (Caller(request, issuer) is null)
        {
            return Unauthorized();
        }

        var ids = request.Query["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length is 0 or > MaxBulkIds)
        {
            return ApiFailure(StatusCodes.Status400BadRequest, "bad_request", "ids must have between 1 and 20 item ids.");
        }

        var attributes = request.Query["attributes"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(attribute => attribute.StartsWith("body.", StringComparison.Ordinal))
            .Select(attribute => attribute["body.".Length..])
            .ToHashSet(StringComparer.Ordinal);

        var entries = ids.Select(id => state.FindItem(id) is { } item
            ? new BulkEntry(id, StatusCodes.Status200OK, Body(item, attributes))
            : new BulkEntry(id, StatusCodes.Status404NotFound, new ApiError($"Item with id {id} not found", "not_found", StatusCodes.Status404NotFound, [])));
        return Results.Json(entries.ToList(), MlJson.Options);
    }

    /// <summary>Corpo do item com os campos pedidos em <c>attributes</c> (todos, quando não há filtro).</summary>
    private static Dictionary<string, object?> Body(ItemInfo item, HashSet<string> attributes)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = item.Id,
            ["seller_id"] = item.SellerId,
            ["title"] = item.Title,
            ["price"] = item.Price,
            ["currency_id"] = item.CurrencyId,
            ["available_quantity"] = item.AvailableQuantity,
            ["sold_quantity"] = item.SoldQuantity,
            ["status"] = item.Status,
            ["permalink"] = item.Permalink,
            ["thumbnail"] = item.Thumbnail,
            ["secure_thumbnail"] = item.SecureThumbnail,
            ["listing_type_id"] = item.ListingTypeId,
            ["last_updated"] = MlJson.Date(item.LastUpdated),
        };
        return attributes.Count == 0
            ? body
            : body.Where(field => attributes.Contains(field.Key)).ToDictionary(StringComparer.Ordinal);
    }

    /// <summary>Parâmetro recusado na autorização e o motivo; <c>null</c> quando todos são aceitos.</summary>
    private static (string Parameter, string Reason)? RejectedParameter(Dictionary<string, string?> parameters, SimulatorOptions options)
    {
        if (!string.Equals(parameters["client_id"], options.ClientId, StringComparison.Ordinal))
        {
            return ("client_id", "Aplicativo desconhecido: o client_id não é o configurado em Simulator:ClientId.");
        }

        if (!options.AllowedRedirectUris.Contains(parameters["redirect_uri"] ?? string.Empty, StringComparer.Ordinal))
        {
            return ("redirect_uri", "O redirect_uri não está em Simulator:AllowedRedirectUris.");
        }

        if (parameters["response_type"] != "code")
        {
            return ("response_type", "Só response_type=code é aceito.");
        }

        if (parameters["code_challenge_method"] != "S256")
        {
            return ("code_challenge_method", "Só code_challenge_method=S256 é aceito (PKCE).");
        }

        if (string.IsNullOrEmpty(parameters["code_challenge"]))
        {
            return ("code_challenge", "O code_challenge (PKCE) é obrigatório.");
        }

        return null;
    }

    /// <summary>Vendedor dono do <c>Authorization: Bearer</c> válido; <c>null</c> sem token ou com token inválido.</summary>
    private static long? Caller(HttpRequest request, TokenIssuer issuer)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? issuer.ValidateAccessToken(header[prefix.Length..].Trim())
            : null;
    }

    private static bool TryInt(string? value, int fallback, out int result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = fallback;
            return true;
        }

        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    private static IResult Html(string html, int status) => Results.Content(html, "text/html; charset=utf-8", statusCode: status);

    private static IResult Unauthorized() =>
        ApiFailure(StatusCodes.Status401Unauthorized, "unauthorized", "invalid access token");

    private static IResult ApiFailure(int status, string error, string message) =>
        Results.Json(new ApiError(message, error, status, []), MlJson.Options, statusCode: status);

    private static IResult OAuthFailure(int status, string error, string description) =>
        Results.Json(new OAuthError(error, description, status, []), MlJson.Options, statusCode: status);
}
