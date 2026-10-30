using Vitrine.MercadoLivre.Domain.Accounts;

namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>Persistência das contas conectadas (gravadas no commit da <see cref="IMercadoLivreUnitOfWork"/>).</summary>
public interface IMercadoLivreAccounts
{
    void Add(MercadoLivreAccount account);

    /// <summary>Apaga a conta e os tokens dela.</summary>
    void Remove(MercadoLivreAccount account);

    /// <summary>Conta com o id do vendedor no ML, em qualquer workspace (<c>ml_user_id</c> é único).</summary>
    Task<MercadoLivreAccount?> FindByMlUserIdAsync(long mlUserId, CancellationToken cancellationToken);

    /// <summary>Conta do workspace; nula se não existe ou é de outro workspace.</summary>
    Task<MercadoLivreAccount?> FindInWorkspaceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>Contas do workspace em ordem de conexão, sem tokens.</summary>
    Task<IReadOnlyList<MercadoLivreAccountView>> ListAsync(Guid workspaceId, CancellationToken cancellationToken);
}

/// <summary>Conta para listagem: nunca contém tokens.</summary>
public sealed record MercadoLivreAccountView(
    Guid AccountId, long MlUserId, string Nickname, string SiteId, MercadoLivreAccountStatus Status, DateTimeOffset ConnectedAt);
