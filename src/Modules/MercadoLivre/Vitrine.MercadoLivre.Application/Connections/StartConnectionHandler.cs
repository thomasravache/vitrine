using Vitrine.Identity.PublicApi;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Domain.Authorizations;

namespace Vitrine.MercadoLivre.Application.Connections;

/// <summary>
/// Inicia a conexão de uma conta do ML (só o Owner): gera PKCE S256 e <c>state</c>, grava a autorização pendente (state
/// só como hash, verifier cifrado pela persistência, validade de 10 min) e devolve a URL de autorização do ML.
/// </summary>
public sealed class StartConnectionHandler(
    IWorkspaceMembership membership,
    IMercadoLivreOAuth oauth,
    IPendingAuthorizations authorizations,
    IMercadoLivreUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<StartConnectionResult> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var member = await membership.GetAsync(userId, cancellationToken);
        if (!WorkspaceRoles.IsOwner(member))
        {
            return new StartConnectionResult.Forbidden();
        }

        if (!oauth.IsConfigured)
        {
            return new StartConnectionResult.NotConfigured();
        }

        var secrets = Pkce.Generate();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        authorizations.Add(PendingAuthorization.Create(
            member.WorkspaceId, userId, Pkce.HashState(secrets.State), secrets.CodeVerifier, clock.GetUtcNow()));
        await transaction.CommitAsync(cancellationToken);

        return new StartConnectionResult.Started(oauth.AuthorizationUrl(secrets.State, secrets.CodeChallenge));
    }
}

/// <summary>Resultado de <see cref="StartConnectionHandler"/>.</summary>
public abstract record StartConnectionResult
{
    private StartConnectionResult()
    {
    }

    /// <summary>Autorização pendente gravada; o navegador segue para <paramref name="AuthorizationUrl"/>.</summary>
    public sealed record Started(Uri AuthorizationUrl) : StartConnectionResult;

    /// <summary>Usuário sem workspace ou que não é Owner.</summary>
    public sealed record Forbidden : StartConnectionResult;

    /// <summary><c>ClientId</c>, <c>ClientSecret</c> ou <c>RedirectUri</c> ausentes.</summary>
    public sealed record NotConfigured : StartConnectionResult;
}
