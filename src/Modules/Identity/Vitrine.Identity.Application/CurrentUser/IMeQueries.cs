namespace Vitrine.Identity.Application.CurrentUser;

/// <summary>Leitura de "quem sou eu": o usuário autenticado e o próprio workspace (ADR-0007: nunca outro).</summary>
public interface IMeQueries
{
    /// <summary>Usuário e workspace dele; nulo se a conta não existe mais.</summary>
    Task<MeView?> FindAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Usuário autenticado e o workspace dele.</summary>
public sealed record MeView(Guid UserId, string Name, string Email, MeWorkspaceView Workspace);

/// <summary>Workspace do usuário e o papel dele.</summary>
public sealed record MeWorkspaceView(Guid Id, string Name, string Role);
