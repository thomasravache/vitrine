using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Domain.Accounts;

namespace Vitrine.MercadoLivre.Infrastructure.OAuth;

/// <summary>
/// OAuth do Mercado Livre pelo <c>HttpClient</c> nomeado <see cref="MercadoLivreModule.HttpClientName"/> (ADR-0007):
/// <c>POST /oauth/token</c> (form-urlencoded) e <c>GET /users/me</c>. Os logs trazem só o status e o <c>error</c> do ML;
/// nunca tokens, code, verifier nem o client secret (ADR-0006).
/// </summary>
internal sealed partial class MercadoLivreOAuthClient(
    IHttpClientFactory httpClients,
    IOptions<MercadoLivreOptions> options,
    ILogger<MercadoLivreOAuthClient> logger) : IMercadoLivreOAuth
{
    private const string InvalidGrant = "invalid_grant";

    private MercadoLivreOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;

    public Uri AuthorizationUrl(string state, string codeChallenge)
    {
        var url = QueryHelpers.AddQueryString(
            $"{Options.AuthorizationBaseUrl.TrimEnd('/')}/authorization",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["response_type"] = "code",
                ["client_id"] = Options.ClientId,
                ["redirect_uri"] = Options.RedirectUri,
                ["state"] = state,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
            });
        return new Uri(url);
    }

    public async Task<TokenExchange> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri("oauth/token"))
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"),
                new("client_id", Options.ClientId),
                new("client_secret", Options.ClientSecret),
                new("code", code),
                new("redirect_uri", Options.RedirectUri),
                new("code_verifier", codeVerifier),
            ]),
        };

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                if (response.StatusCode == HttpStatusCode.BadRequest && error == InvalidGrant)
                {
                    Log.CodeRejected(logger);
                    return new TokenExchange.CodeRejected();
                }

                Log.TokenExchangeFailed(logger, (int)response.StatusCode, error);
                return new TokenExchange.Unavailable();
            }

            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (body is null || string.IsNullOrEmpty(body.AccessToken) || body.UserId <= 0 || body.ExpiresIn <= 0)
            {
                Log.InvalidResponse(logger, "/oauth/token");
                return new TokenExchange.Unavailable();
            }

            if (string.IsNullOrEmpty(body.RefreshToken))
            {
                Log.MissingOfflineAccess(logger);
                return new TokenExchange.Unavailable();
            }

            return new TokenExchange.Issued(
                new MercadoLivreTokens(body.AccessToken, body.RefreshToken, TimeSpan.FromSeconds(body.ExpiresIn)), body.UserId);
        }
        catch (Exception exception) when (IsUnavailability(exception, cancellationToken))
        {
            Log.Unreachable(logger, "/oauth/token", exception.GetType().Name);
            return new TokenExchange.Unavailable();
        }
    }

    public async Task<MercadoLivreSeller?> GetSellerAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri("users/me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Log.UsersMeFailed(logger, (int)response.StatusCode, await ReadErrorAsync(response, cancellationToken));
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<UserResponse>(cancellationToken);
            if (body is null || body.Id <= 0 || string.IsNullOrEmpty(body.Nickname) || string.IsNullOrEmpty(body.SiteId))
            {
                Log.InvalidResponse(logger, "/users/me");
                return null;
            }

            // Só id, apelido e site: e-mail, nome e documento devolvidos pelo ML não são lidos nem guardados.
            return new MercadoLivreSeller(body.Id, body.Nickname, body.SiteId);
        }
        catch (Exception exception) when (IsUnavailability(exception, cancellationToken))
        {
            Log.Unreachable(logger, "/users/me", exception.GetType().Name);
            return null;
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var client = httpClients.CreateClient(MercadoLivreModule.HttpClientName);
        return client.SendAsync(request, cancellationToken);
    }

    private Uri ApiUri(string path) => new($"{Options.ApiBaseUrl.TrimEnd('/')}/{path}");

    // Falha de rede, timeout (Http.Resilience ou HttpClient), circuito aberto ou JSON inválido; o cancelamento pedido
    // pelo chamador (requisição abortada) continua propagando.
    private static bool IsUnavailability(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or ExecutionRejectedException or JsonException or NotSupportedException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>
    /// Campo <c>error</c> do corpo de erro do ML (<c>{ error, message, status }</c>), só se for um identificador simples
    /// (ex.: <c>invalid_client</c>); a <c>message</c> nunca é lida, para não levar dados ao log.
    /// </summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken);
            return body?.Error is { } error && ErrorIdentifier().IsMatch(error) ? error : "desconhecido";
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return "desconhecido";
        }
    }

    [GeneratedRegex("^[a-z_]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ErrorIdentifier();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("user_id")] long UserId,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken)
    {
        // Sem os tokens: um record registrado em log não pode vazá-los.
        public override string ToString() => nameof(TokenResponse);
    }

    private sealed record UserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("nickname")] string? Nickname,
        [property: JsonPropertyName("site_id")] string? SiteId);

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);

    /// <summary>Mensagens de log (sem segredos: nunca tokens, code, verifier nem client secret).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Mercado Livre recusou o code de autorização (invalid_grant).")]
        public static partial void CodeRejected(ILogger logger);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Mercado Livre recusou a troca do code: status {Status}, error {MlError}.")]
        public static partial void TokenExchangeFailed(ILogger logger, int status, string mlError);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Resposta do Mercado Livre sem refresh_token: escopo offline_access ausente no app do DevCenter.")]
        public static partial void MissingOfflineAccess(ILogger logger);

        [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Mercado Livre respondeu {Path} em formato inesperado.")]
        public static partial void InvalidResponse(ILogger logger, string path);

        [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Mercado Livre indisponível em {Path} ({Failure}).")]
        public static partial void Unreachable(ILogger logger, string path, string failure);

        [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Mercado Livre recusou /users/me: status {Status}, error {MlError}.")]
        public static partial void UsersMeFailed(ILogger logger, int status, string mlError);
    }
}
