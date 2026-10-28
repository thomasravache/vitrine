using Vitrine.Identity.Domain.Workspaces;

namespace Vitrine.UnitTests.Identity;

public sealed class WorkspaceTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("spec", "SPEC-0006:UT-01")]
    public void CreateFor_cria_workspace_com_unico_membro_Owner_e_gera_evento_de_criacao()
    {
        // Dado um usuário recém-cadastrado
        var user = new RegisteredUser(Guid.NewGuid(), "Marina Souza");

        // Quando o workspace é criado para ele
        var workspace = Workspace.CreateFor(user, CreatedAt);

        // Então o workspace tem identidade, nome e um único membro: o usuário, com papel Owner
        Assert.NotEqual(Guid.Empty, workspace.Id);
        Assert.False(string.IsNullOrWhiteSpace(workspace.Name), "O workspace precisa de um nome (exibido em GET /api/v1/me).");
        Assert.Equal(CreatedAt, workspace.CreatedAt);
        var member = Assert.Single(workspace.Members);
        Assert.Equal(user.Id, member.UserId);
        Assert.Equal(workspace.Id, member.WorkspaceId);
        Assert.Equal(WorkspaceRole.Owner, member.Role);

        // E gera o evento de domínio de criação com o workspace, o dono e o instante
        var domainEvent = Assert.IsType<WorkspaceCreatedDomainEvent>(Assert.Single(workspace.DomainEvents));
        Assert.Equal(new WorkspaceCreatedDomainEvent(workspace.Id, user.Id, CreatedAt), domainEvent);
    }

    [Fact]
    [Trait("spec", "SPEC-0006:UT-01")]
    public void CreateFor_gera_workspaces_distintos_para_usuarios_distintos()
    {
        // Dado dois usuários recém-cadastrados; quando cada um ganha seu workspace
        var first = Workspace.CreateFor(new RegisteredUser(Guid.NewGuid(), "Marina Souza"), CreatedAt);
        var second = Workspace.CreateFor(new RegisteredUser(Guid.NewGuid(), "Carlos Lima"), CreatedAt);

        // Então são inquilinos diferentes
        Assert.NotEqual(first.Id, second.Id);
    }
}
