namespace Vitrine.Identity.Domain.Workspaces;

/// <summary>Papel de um usuário num workspace (ADR-0007).</summary>
public enum WorkspaceRole
{
    /// <summary>Dono do workspace (quem se cadastrou).</summary>
    Owner,

    /// <summary>Assistente convidado pelo dono (fora do escopo da SPEC-0006).</summary>
    Member,
}
