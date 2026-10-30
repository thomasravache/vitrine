using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.PublicApi;

namespace Vitrine.Identity.Application.Membership;

/// <summary><see cref="IWorkspaceMembership"/> sobre o repositório de workspaces do Identity (SPEC-0020).</summary>
public sealed class WorkspaceMembershipQuery(IWorkspaceRepository workspaces) : IWorkspaceMembership
{
    public async Task<WorkspaceMembership?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var workspace = await workspaces.FindByMemberAsync(userId, cancellationToken);
        var member = workspace?.Members.FirstOrDefault(candidate => candidate.UserId == userId);
        return workspace is null || member is null ? null : new WorkspaceMembership(workspace.Id, member.Role.ToString());
    }
}
