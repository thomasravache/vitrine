namespace Vitrine.MercadoLivreSimulator;

/// <summary>Tokens emitidos para um vendedor (<c>expires_in</c> em segundos).</summary>
public sealed record IssuedTokens(long SellerId, string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Emissor de tokens do simulador: refresh token de uso único e rotacionado (só o último emitido vale), revogação e
/// expiração forçada do access token por vendedor.
/// </summary>
public sealed class TokenIssuer
{
    /// <summary>Emissor com o relógio <paramref name="timeProvider"/> e a validade <paramref name="accessTokenLifetime"/>.</summary>
    public TokenIssuer(TimeProvider timeProvider, TimeSpan accessTokenLifetime) => throw new NotImplementedException();

    /// <summary>Emite um par novo de tokens para o vendedor.</summary>
    public IssuedTokens Issue(long sellerId) => throw new NotImplementedException();

    /// <summary>Renova com o último refresh token emitido e não revogado; senão <c>null</c> (<c>invalid_grant</c>).</summary>
    public IssuedTokens? Refresh(string refreshToken) => throw new NotImplementedException();

    /// <summary>Vendedor dono do access token válido; <c>null</c> se desconhecido, expirado ou revogado (401).</summary>
    public long? ValidateAccessToken(string accessToken) => throw new NotImplementedException();

    /// <summary>Invalida access e refresh tokens do vendedor.</summary>
    public void Revoke(long sellerId) => throw new NotImplementedException();

    /// <summary>Invalida só o access token atual do vendedor (o refresh continua valendo).</summary>
    public void ExpireAccessToken(long sellerId) => throw new NotImplementedException();
}
