namespace Vitrine.MercadoLivre.Domain.Accounts;

/// <summary>Tokens devolvidos por <c>POST /oauth/token</c> (cifrados em repouso; nunca saem da API nem vão para log).</summary>
/// <param name="AccessToken">Access token.</param>
/// <param name="RefreshToken">Refresh token (exige o escopo <c>offline_access</c>).</param>
/// <param name="ExpiresIn">Validade do access token (<c>expires_in</c>).</param>
public sealed record MercadoLivreTokens(string AccessToken, string RefreshToken, TimeSpan ExpiresIn)
{
    /// <summary>Sem os valores dos tokens: um record registrado em log não pode vazá-los.</summary>
    public override string ToString() => $"MercadoLivreTokens {{ ExpiresIn = {ExpiresIn} }}";
}
