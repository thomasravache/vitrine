using Vitrine.MercadoLivre.Domain.Accounts;

namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>
/// OAuth do Mercado Livre (ADR-0007): URL de autorização, troca do code por tokens e leitura de <c>/users/me</c>.
/// Nenhuma implementação registra tokens, code, verifier ou o client secret em log.
/// </summary>
public interface IMercadoLivreOAuth
{
    /// <summary>Verdadeiro quando <c>ClientId</c>, <c>ClientSecret</c> e <c>RedirectUri</c> estão configurados.</summary>
    bool IsConfigured { get; }

    /// <summary>URL da página de autorização do ML com PKCE S256 e o <paramref name="state"/>.</summary>
    Uri AuthorizationUrl(string state, string codeChallenge);

    /// <summary>Troca o code de autorização (com o <c>code_verifier</c>) por tokens.</summary>
    Task<TokenExchange> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken);

    /// <summary>Vendedor dono do access token (<c>GET /users/me</c>).</summary>
    Task<MercadoLivreSeller?> GetSellerAsync(string accessToken, CancellationToken cancellationToken);
}

/// <summary>Resultado de <see cref="IMercadoLivreOAuth.ExchangeCodeAsync"/>.</summary>
public abstract record TokenExchange
{
    private TokenExchange()
    {
    }

    /// <summary>Tokens emitidos para o vendedor <paramref name="MlUserId"/> (<c>user_id</c> da resposta).</summary>
    public sealed record Issued(MercadoLivreTokens Tokens, long MlUserId) : TokenExchange;

    /// <summary>O ML recusou o code (<c>invalid_grant</c>).</summary>
    public sealed record CodeRejected : TokenExchange;

    /// <summary>Falha, timeout, limite ou erro de cliente do ML (ou resposta sem <c>refresh_token</c>).</summary>
    public sealed record Unavailable : TokenExchange;
}
