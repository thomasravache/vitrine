using System.Diagnostics.CodeAnalysis;
using Vitrine.Identity.PublicApi;

namespace Vitrine.MercadoLivre.Application;

/// <summary>Papéis do workspace (contrato do <see cref="IWorkspaceMembership"/>) que o módulo usa.</summary>
public static class WorkspaceRoles
{
    public const string Owner = "Owner";

    /// <summary>Verdadeiro quando o usuário pertence a um workspace e é o Owner dele.</summary>
    public static bool IsOwner([NotNullWhen(true)] WorkspaceMembership? membership) =>
        membership is not null && string.Equals(membership.Role, Owner, StringComparison.Ordinal);
}
