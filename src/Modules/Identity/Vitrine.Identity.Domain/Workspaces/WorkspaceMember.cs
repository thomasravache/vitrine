namespace Vitrine.Identity.Domain.Workspaces;

/// <summary>Vínculo de um usuário com um workspace e seu papel.</summary>
public sealed class WorkspaceMember
{
    private WorkspaceMember()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public WorkspaceRole Role { get; private set; }
}
