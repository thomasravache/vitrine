namespace Vitrine.Identity.Domain.Workspaces;

/// <summary>Workspace: o inquilino do Vitrine (ADR-0007). Cada cadastro cria um workspace com um único Owner.</summary>
public sealed class Workspace
{
    private readonly List<WorkspaceMember> _members = [];
    private readonly List<IDomainEvent> _domainEvents = [];

    // Materialização pelo EF Core.
    private Workspace()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<WorkspaceMember> Members => _members.AsReadOnly();

    /// <summary>Eventos de domínio gerados e ainda não publicados.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Cria o workspace de um usuário recém-cadastrado, com ele como único membro <see cref="WorkspaceRole.Owner"/>.</summary>
    public static Workspace CreateFor(RegisteredUser user, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(user.Name);

        var workspace = new Workspace
        {
            Id = Guid.CreateVersion7(createdAt),
            Name = user.Name.Trim(),
            CreatedAt = createdAt,
        };
        workspace._members.Add(new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.Owner));
        workspace._domainEvents.Add(new WorkspaceCreatedDomainEvent(workspace.Id, user.Id, createdAt));
        return workspace;
    }
}
