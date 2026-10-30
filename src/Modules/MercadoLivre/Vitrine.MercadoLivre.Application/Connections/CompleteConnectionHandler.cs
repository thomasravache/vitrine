using Vitrine.Identity.PublicApi;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Domain.Authorizations;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.MercadoLivre.Application.Connections;

/// <summary>
/// Conclui a conexão (só o Owner) com o <c>code</c> e o <c>state</c> devolvidos pelo ML à SPA:
/// <list type="number">
/// <item>consome a autorização pendente (marcada como usada antes de chamar o ML: um state nunca serve duas vezes);</item>
/// <item>troca o code por tokens com o <c>code_verifier</c> e lê <c>/users/me</c>;</item>
/// <item>grava ou reconecta a conta (tokens cifrados) e publica <see cref="MercadoLivreAccountConnected"/> pelo outbox,
/// na mesma transação.</item>
/// </list>
/// </summary>
public sealed class CompleteConnectionHandler(
    IWorkspaceMembership membership,
    IMercadoLivreOAuth oauth,
    IPendingAuthorizations authorizations,
    IMercadoLivreAccounts accounts,
    IMercadoLivreUnitOfWork unitOfWork,
    IConnectionMetrics metrics,
    TimeProvider clock)
{
    public const int CodeMaxLength = 512;
    public const int StateMaxLength = 128;

    /// <summary>Único site atendido (épico SPEC-0019).</summary>
    public const string SupportedSiteId = "MLB";

    public async Task<CompleteConnectionResult> HandleAsync(Guid userId, string? code, string? state, CancellationToken cancellationToken)
    {
        var member = await membership.GetAsync(userId, cancellationToken);
        if (!WorkspaceRoles.IsOwner(member))
        {
            return new CompleteConnectionResult.Forbidden();
        }

        if (!oauth.IsConfigured)
        {
            return new CompleteConnectionResult.NotConfigured();
        }

        var errors = Validate(code, state);
        if (errors.Count > 0)
        {
            return new CompleteConnectionResult.Invalid(errors);
        }

        var result = await CompleteAsync(member.WorkspaceId, userId, code!, state!, cancellationToken);
        metrics.Record(result switch
        {
            CompleteConnectionResult.Connected { Reconnected: true } => ConnectionOutcome.Reconnected,
            CompleteConnectionResult.Connected => ConnectionOutcome.Connected,
            CompleteConnectionResult.StateInvalid => ConnectionOutcome.StateInvalid,
            CompleteConnectionResult.CodeInvalid => ConnectionOutcome.CodeInvalid,
            CompleteConnectionResult.SiteNotSupported => ConnectionOutcome.SiteNotSupported,
            CompleteConnectionResult.AccountInUse => ConnectionOutcome.InUse,
            _ => ConnectionOutcome.MlUnavailable,
        });
        return result;
    }

    private async Task<CompleteConnectionResult> CompleteAsync(
        Guid workspaceId, Guid userId, string code, string state, CancellationToken cancellationToken)
    {
        var codeVerifier = await ConsumeAuthorizationAsync(workspaceId, userId, state, cancellationToken);
        if (codeVerifier is null)
        {
            return new CompleteConnectionResult.StateInvalid();
        }

        var exchange = await oauth.ExchangeCodeAsync(code, codeVerifier, cancellationToken);
        if (exchange is TokenExchange.CodeRejected)
        {
            return new CompleteConnectionResult.CodeInvalid();
        }

        if (exchange is not TokenExchange.Issued issued)
        {
            return new CompleteConnectionResult.MlUnavailable();
        }

        var seller = await oauth.GetSellerAsync(issued.Tokens.AccessToken, cancellationToken);
        if (seller is null)
        {
            return new CompleteConnectionResult.MlUnavailable();
        }

        if (!string.Equals(seller.SiteId, SupportedSiteId, StringComparison.Ordinal))
        {
            return new CompleteConnectionResult.SiteNotSupported();
        }

        if (seller.MlUserId != issued.MlUserId)
        {
            return new CompleteConnectionResult.MlUnavailable();
        }

        return await SaveAccountAsync(workspaceId, seller, issued.Tokens, cancellationToken);
    }

    /// <summary>Consome a autorização pendente; devolve o verifier ou nulo quando o state é inválido.</summary>
    private async Task<string?> ConsumeAuthorizationAsync(Guid workspaceId, Guid userId, string state, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var authorization = await authorizations.FindByStateHashAsync(Pkce.HashState(state), cancellationToken);
        if (authorization is null
            || authorization.WorkspaceId != workspaceId
            || authorization.Consume(userId, clock.GetUtcNow()) != PendingAuthorizationConsumption.Accepted)
        {
            return null;
        }

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ConcurrentChangeException)
        {
            // Outra conclusão consumiu o mesmo state ao mesmo tempo.
            return null;
        }

        return authorization.CodeVerifier;
    }

    private async Task<CompleteConnectionResult> SaveAccountAsync(
        Guid workspaceId, MercadoLivreSeller seller, MercadoLivreTokens tokens, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var account = await accounts.FindByMlUserIdAsync(seller.MlUserId, cancellationToken);
        if (account is not null && account.WorkspaceId != workspaceId)
        {
            // Tokens descartados: nada é gravado.
            return new CompleteConnectionResult.AccountInUse();
        }

        var reconnected = account is not null;
        if (account is null)
        {
            account = MercadoLivreAccount.Connect(workspaceId, seller, tokens, now);
            accounts.Add(account);
        }
        else
        {
            account.Reconnect(seller, tokens, now);
        }

        foreach (var connected in account.DomainEvents.OfType<MercadoLivreAccountConnectedDomainEvent>())
        {
            await unitOfWork.PublishAsync(new MercadoLivreAccountConnected(
                connected.AccountId, connected.WorkspaceId, connected.MlUserId, connected.SiteId, connected.Reconnected, connected.OccurredAt));
        }

        account.ClearDomainEvents();

        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ConcurrentChangeException)
        {
            // A mesma conta do ML foi gravada ao mesmo tempo por outra conclusão.
            return new CompleteConnectionResult.AccountInUse();
        }

        return new CompleteConnectionResult.Connected(account.Id, account.MlUserId, account.Nickname, reconnected);
    }

    private static Dictionary<string, string[]> Validate(string? code, string? state)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(code) || code.Length > CodeMaxLength)
        {
            errors["code"] = [$"Informe o code de autorização (de 1 a {CodeMaxLength} caracteres)."];
        }

        if (string.IsNullOrEmpty(state) || state.Length > StateMaxLength)
        {
            errors["state"] = [$"Informe o state (de 1 a {StateMaxLength} caracteres)."];
        }

        return errors;
    }
}

/// <summary>Resultado de <see cref="CompleteConnectionHandler"/>.</summary>
public abstract record CompleteConnectionResult
{
    private CompleteConnectionResult()
    {
    }

    /// <summary>Conta gravada (nova) ou reconectada (mesmo vendedor no mesmo workspace).</summary>
    public sealed record Connected(Guid AccountId, long MlUserId, string Nickname, bool Reconnected) : CompleteConnectionResult;

    public sealed record Forbidden : CompleteConnectionResult;

    public sealed record NotConfigured : CompleteConnectionResult;

    /// <summary>Corpo inválido: erros por campo (<c>code</c>, <c>state</c>).</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CompleteConnectionResult;

    /// <summary>State inexistente, expirado, já usado ou de outro usuário.</summary>
    public sealed record StateInvalid : CompleteConnectionResult;

    /// <summary>O ML recusou o code (<c>invalid_grant</c>).</summary>
    public sealed record CodeInvalid : CompleteConnectionResult;

    public sealed record MlUnavailable : CompleteConnectionResult;

    /// <summary>Conta de um site diferente de MLB.</summary>
    public sealed record SiteNotSupported : CompleteConnectionResult;

    /// <summary>A conta do ML já está conectada em outro workspace.</summary>
    public sealed record AccountInUse : CompleteConnectionResult;
}
