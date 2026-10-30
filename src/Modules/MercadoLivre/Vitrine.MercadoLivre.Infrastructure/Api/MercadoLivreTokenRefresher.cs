using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Vitrine.MercadoLivre.Domain.Accounts;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <c>POST /oauth/token</c> com <c>grant_type=refresh_token</c> pelo cliente <see cref="MercadoLivreModule.OAuthHttpClientName"/>
/// (SPEC-0021): uma única tentativa, nunca retentada (o refresh token é de uso único), limitada pelo timeout de 10 s
/// do cliente. Quem chama passa um token desacoplado da tentativa HTTP em curso. Logs só com o status e o
/// <c>error</c> do ML; nunca tokens nem o client secret (ADR-0006).
/// </summary>
internal sealed partial class MercadoLivreTokenRefresher(
    IHttpClientFactory httpClients,
    IOptions<MercadoLivreOptions> options,
    ILogger<MercadoLivreTokenRefresher> logger)
{
    private const string InvalidGrant = "invalid_grant";

    public async Task<TokenRefresh> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{settings.ApiBaseUrl.TrimEnd('/')}/oauth/token"))
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "refresh_token"),
                new("client_id", settings.ClientId),
                new("client_secret", settings.ClientSecret),
                new("refresh_token", refreshToken),
            ]),
        };
        request.Headers.Accept.Add(new("application/json"));

        try
        {
            using var response = await httpClients.CreateClient(MercadoLivreModule.OAuthHttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                if (response.StatusCode == HttpStatusCode.BadRequest && error == InvalidGrant)
                {
                    Log.Rejected(logger);
                    return new TokenRefresh.Rejected();
                }

                Log.Failed(logger, (int)response.StatusCode, error);
                return new TokenRefresh.Failed();
            }

            var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            if (body is null || string.IsNullOrEmpty(body.AccessToken) || string.IsNullOrEmpty(body.RefreshToken) || body.ExpiresIn <= 0)
            {
                Log.InvalidResponse(logger);
                return new TokenRefresh.Failed();
            }

            return new TokenRefresh.Renewed(
                new MercadoLivreTokens(body.AccessToken, body.RefreshToken, TimeSpan.FromSeconds(body.ExpiresIn)));
        }
        // Qualquer falha (inclusive timeout do cliente ou cancelamento) é Failed: quem chama nunca retenta o refresh.
        catch (Exception exception) when (exception is HttpRequestException or ExecutionRejectedException or JsonException
            or NotSupportedException or OperationCanceledException)
        {
            Log.Unreachable(logger, exception.GetType().Name);
            return new TokenRefresh.Failed();
        }
    }

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
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken)
    {
        // Sem os tokens: um record registrado em log não pode vazá-los.
        public override string ToString() => nameof(TokenResponse);
    }

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string? Error);

    /// <summary>Mensagens de log (sem segredos).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Mercado Livre recusou o refresh token (invalid_grant): a conta precisa ser reconectada.")]
        public static partial void Rejected(ILogger logger);

        [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Renovação do token do Mercado Livre falhou: status {Status}, error {MlError}.")]
        public static partial void Failed(ILogger logger, int status, string mlError);

        [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Mercado Livre respondeu /oauth/token (refresh) em formato inesperado.")]
        public static partial void InvalidResponse(ILogger logger);

        [LoggerMessage(EventId = 14, Level = LogLevel.Error, Message = "Mercado Livre indisponível na renovação do token ({Failure}).")]
        public static partial void Unreachable(ILogger logger, string failure);
    }
}

/// <summary>Resultado de <see cref="MercadoLivreTokenRefresher.RefreshAsync"/>.</summary>
internal abstract record TokenRefresh
{
    private TokenRefresh()
    {
    }

    public sealed record Renewed(MercadoLivreTokens Tokens) : TokenRefresh;

    /// <summary><c>invalid_grant</c>: o refresh token não vale mais.</summary>
    public sealed record Rejected : TokenRefresh;

    /// <summary>Outro erro, timeout ou falha de rede: nada muda na conta.</summary>
    public sealed record Failed : TokenRefresh;
}
