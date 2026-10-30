using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Anexa <c>Authorization: Bearer</c> às chamadas do cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0021),
/// só quando a requisição vai para o <c>ApiBaseUrl</c> (mesmo esquema, host e porta); a conta vem de
/// <see cref="AccountId"/> nas opções da requisição. Um 401 do ML força uma renovação e repete a requisição uma vez; um
/// novo 401 volta para quem chamou.
/// </summary>
internal sealed class MercadoLivreAuthenticationHandler : DelegatingHandler
{
    /// <summary>Conta em nome da qual a requisição é feita.</summary>
    public static readonly HttpRequestOptionsKey<Guid> AccountId = new("Vitrine.MercadoLivre.AccountId");

    private const string Scheme = "Bearer";

    private readonly IMercadoLivreAccessTokenProvider _tokens;
    private readonly IOptions<MercadoLivreOptions> _options;

    public MercadoLivreAuthenticationHandler(IMercadoLivreAccessTokenProvider tokens, IOptions<MercadoLivreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        _tokens = tokens;
        _options = options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsApiRequest(request.RequestUri) || !request.Options.TryGetValue(AccountId, out var accountId))
        {
            // Outro host (ou sem conta): segue sem token — o token só vai ao ApiBaseUrl (ADR-0006).
            request.Headers.Authorization = null;
            return await base.SendAsync(request, cancellationToken);
        }

        var token = await _tokens.GetAccessTokenAsync(accountId, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Token rejeitado: uma renovação forçada (só se ainda for o token rejeitado) e uma nova tentativa.
        response.Dispose();
        var renewed = await _tokens.RenewAfterRejectionAsync(accountId, token, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, renewed);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsApiRequest(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || !Uri.TryCreate(_options.Value.ApiBaseUrl, UriKind.Absolute, out var api))
        {
            return false;
        }

        return string.Equals(uri.Scheme, api.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, api.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == api.Port;
    }
}
