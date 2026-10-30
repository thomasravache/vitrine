using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.PublicApi;

namespace Vitrine.Identity.Application.Membership;

/// <summary><see cref="IWorkspaceMembership"/> sobre o repositório de workspaces do Identity (SPEC-0020). Scaffold do contrato.</summary>
public sealed class WorkspaceMembershipQuery(IWorkspaceRepository workspaces) : IWorkspaceMembership
{
    public Task<WorkspaceMembership?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new NotImplementedException($"SPEC-0020: implementado na fase Green ({workspaces.GetType().Name}).");
}
