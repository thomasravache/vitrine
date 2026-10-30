using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Decide se o access token precisa ser renovado antes da chamada (SPEC-0021): só com menos de
/// <see cref="MercadoLivreOptions.RefreshSkew"/> restante, ou quando forçado depois de um 401.
/// </summary>
internal sealed class TokenRefreshPolicy
{
    private readonly TimeProvider _clock;
    private readonly IOptions<MercadoLivreOptions> _options;

    public TokenRefreshPolicy(TimeProvider timeProvider, IOptions<MercadoLivreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _clock = timeProvider;
        _options = options;
    }

    /// <summary>Verdadeiro quando o token que expira em <paramref name="accessTokenExpiresAt"/> deve ser renovado agora.</summary>
    public bool ShouldRefresh(DateTimeOffset accessTokenExpiresAt, bool forced) =>
        forced || accessTokenExpiresAt - _clock.GetUtcNow() < _options.Value.RefreshSkew;
}
