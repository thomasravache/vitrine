using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Anexa <c>Authorization: Bearer</c> às chamadas do cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0021),
/// só quando a requisição vai para o <c>ApiBaseUrl</c>; a conta vem de <see cref="AccountId"/> nas opções da requisição.
/// Scaffold do contrato.
/// </summary>
public sealed class MercadoLivreAuthenticationHandler : DelegatingHandler
{
    /// <summary>Conta em nome da qual a requisição é feita.</summary>
    public static readonly HttpRequestOptionsKey<Guid> AccountId = new("Vitrine.MercadoLivre.AccountId");

    public MercadoLivreAuthenticationHandler(IMercadoLivreAccessTokenProvider tokens, IOptions<MercadoLivreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0021: implementado na fase Green.");
}
