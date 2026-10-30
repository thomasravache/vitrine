using Vitrine.Identity.Domain.Workspaces;

namespace Vitrine.Identity.Application.Abstractions;

/// <summary>Persistência de workspaces (gravados no commit da <see cref="IIdentityUnitOfWork"/>).</summary>
public interface IWorkspaceRepository
{
    void Add(Workspace workspace);

    /// <summary>Workspace do qual o usuário é membro (com os membros carregados); nulo se não pertence a nenhum.</summary>
    Task<Workspace?> FindByMemberAsync(Guid userId, CancellationToken cancellationToken);
}
