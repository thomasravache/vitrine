namespace Vitrine.Identity.PublicApi;

/// <summary>
/// Leitura síncrona entre módulos (ADR-0001, contrato da SPEC-0020): o workspace e o papel do usuário autenticado.
/// </summary>
public interface IWorkspaceMembership
{
    /// <summary>Workspace e papel do usuário; nulo se ele não pertence a nenhum workspace.</summary>
    Task<WorkspaceMembership?> GetAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Workspace do usuário e o papel dele.</summary>
/// <param name="WorkspaceId">Workspace do usuário.</param>
/// <param name="Role">Papel no workspace: <c>"Owner"</c> ou <c>"Member"</c>.</param>
public sealed record WorkspaceMembership(Guid WorkspaceId, string Role);
