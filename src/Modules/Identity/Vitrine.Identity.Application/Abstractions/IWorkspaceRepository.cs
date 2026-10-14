using Vitrine.Identity.Domain.Workspaces;

namespace Vitrine.Identity.Application.Abstractions;

/// <summary>Persistência de workspaces (gravados no commit da <see cref="IIdentityUnitOfWork"/>).</summary>
public interface IWorkspaceRepository
{
    void Add(Workspace workspace);
}
