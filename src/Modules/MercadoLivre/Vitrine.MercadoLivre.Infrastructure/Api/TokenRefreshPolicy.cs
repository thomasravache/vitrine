using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Decide se o access token precisa ser renovado antes da chamada (SPEC-0021): só com menos de
/// <see cref="MercadoLivreOptions.RefreshSkew"/> restante, ou quando forçado depois de um 401. Scaffold do contrato.
/// </summary>
public sealed class TokenRefreshPolicy
{
    public TokenRefreshPolicy(TimeProvider timeProvider, IOptions<MercadoLivreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
    }

    /// <summary>Verdadeiro quando o token que expira em <paramref name="accessTokenExpiresAt"/> deve ser renovado agora.</summary>
    public bool ShouldRefresh(DateTimeOffset accessTokenExpiresAt, bool forced) =>
        throw new NotImplementedException("SPEC-0021: implementado na fase Green.");
}
