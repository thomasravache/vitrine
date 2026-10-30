using Vitrine.Identity.PublicApi;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.MercadoLivre.Application.Accounts;

/// <summary>Contas do workspace do usuário (qualquer membro), em ordem de conexão e sem tokens.</summary>
public sealed class ListAccountsHandler(IWorkspaceMembership membership, IMercadoLivreAccounts accounts)
{
    /// <summary>Contas do workspace; nulo quando o usuário não pertence a nenhum workspace.</summary>
    public async Task<IReadOnlyList<MercadoLivreAccountView>?> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var member = await membership.GetAsync(userId, cancellationToken);
        return member is null ? null : await accounts.ListAsync(member.WorkspaceId, cancellationToken);
    }
}

/// <summary>
/// Desconecta uma conta (só o Owner): apaga a conta e os tokens e publica
/// <see cref="MercadoLivreAccountDisconnected"/> pelo outbox, na mesma transação.
/// </summary>
public sealed class DisconnectAccountHandler(
    IWorkspaceMembership membership,
    IMercadoLivreAccounts accounts,
    IMercadoLivreUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<DisconnectAccountResult> HandleAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var member = await membership.GetAsync(userId, cancellationToken);
        if (!WorkspaceRoles.IsOwner(member))
        {
            return DisconnectAccountResult.Forbidden;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var account = await accounts.FindInWorkspaceAsync(member.WorkspaceId, accountId, cancellationToken);
        if (account is null)
        {
            return DisconnectAccountResult.NotFound;
        }

        accounts.Remove(account);
        await unitOfWork.PublishAsync(new MercadoLivreAccountDisconnected(account.Id, account.WorkspaceId, account.MlUserId, clock.GetUtcNow()));
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ConcurrentChangeException)
        {
            // Outro DELETE simultâneo apagou a conta antes (e publicou o evento); este não confirma nada.
            return DisconnectAccountResult.NotFound;
        }

        return DisconnectAccountResult.Disconnected;
    }
}

/// <summary>Resultado de <see cref="DisconnectAccountHandler"/>.</summary>
public enum DisconnectAccountResult
{
    Disconnected,

    /// <summary>Usuário sem workspace ou que não é Owner.</summary>
    Forbidden,

    /// <summary>Conta inexistente ou de outro workspace (não revela existência).</summary>
    NotFound,
}
