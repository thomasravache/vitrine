namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Provedor do access token de uma conta (SPEC-0021), interno ao módulo: renova sob lock de linha
/// (<c>SELECT … FOR UPDATE</c>), grava os tokens novos cifrados e nunca retenta o refresh. Lança as exceções do
/// <c>PublicApi</c> (conta inexistente, reconexão necessária, ML indisponível).
/// </summary>
public interface IMercadoLivreAccessTokenProvider
{
    /// <summary>Access token válido da conta, renovado antes se faltar menos de <c>RefreshSkew</c>.</summary>
    Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Renovação forçada depois de um 401: renova se o token atual da conta ainda for <paramref name="rejectedAccessToken"/>
    /// e devolve o token vigente.
    /// </summary>
    Task<string> RenewAfterRejectionAsync(Guid accountId, string rejectedAccessToken, CancellationToken cancellationToken);
}
