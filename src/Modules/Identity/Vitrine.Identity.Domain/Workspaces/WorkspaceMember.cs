namespace Vitrine.Identity.Domain.Workspaces;

/// <summary>Vínculo de um usuário com um workspace e seu papel.</summary>
public sealed class WorkspaceMember
{
    internal WorkspaceMember(Guid workspaceId, Guid userId, WorkspaceRole role)
    {
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
    }

    // Materialização pelo EF Core.
    private WorkspaceMember()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public WorkspaceRole Role { get; private set; }
}
