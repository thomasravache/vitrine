using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

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
/// Mercado Livre simulado no WireMock (<c>POST /oauth/token</c> e <c>GET /users/me</c>), com os formatos de resposta
/// da API real. <see cref="Calls"/> é tudo o que a Api chamou.
/// </summary>
internal sealed class MercadoLivreFake(WireMockServer server)
{
    public const string TokenPath = "/oauth/token";
    public const string UsersMePath = "/users/me";

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

    private static IResponseBuilder Delayed(TimeSpan? delay, IResponseBuilder response) =>
        delay is { } wait ? response.WithDelay(wait) : response;

    private static IRequestBuilder TokenRequest() => Request.Create().WithPath(TokenPath).UsingPost();
}
