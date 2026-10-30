using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Application.Membership;
using Vitrine.Identity.Domain.Workspaces;
using Vitrine.Identity.PublicApi;

namespace Vitrine.UnitTests.Identity;

public sealed class WorkspaceMembershipTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("spec", "SPEC-0020:UT-05")]
    public async Task GetAsync_devolve_workspace_e_papel_do_membro()
    {
        // Dado o repositório do Identity com o workspace de um Owner
        var owner = new RegisteredUser(Guid.NewGuid(), "Marina Souza");
        var workspace = Workspace.CreateFor(owner, CreatedAt);
        var membership = new WorkspaceMembershipQuery(new InMemoryWorkspaces(workspace));

        // Quando consultado para o Owner
        var result = await membership.GetAsync(owner.Id, TestContext.Current.CancellationToken);

        // Então devolve o workspace e o papel "Owner"
        Assert.Equal(new WorkspaceMembership(workspace.Id, "Owner"), result);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-05")]
    public async Task GetAsync_devolve_null_para_usuario_sem_workspace()
    {
        // Dado o repositório com o workspace de outro usuário
        var workspace = Workspace.CreateFor(new RegisteredUser(Guid.NewGuid(), "Marina Souza"), CreatedAt);
        var membership = new WorkspaceMembershipQuery(new InMemoryWorkspaces(workspace));

        // Quando consultado para um usuário sem workspace; então devolve null
        Assert.Null(await membership.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    /// <summary>Repositório de workspaces em memória (só leitura por membro).</summary>
    private sealed class InMemoryWorkspaces(params Workspace[] workspaces) : IWorkspaceRepository
    {
        public void Add(Workspace workspace) => throw new NotSupportedException();

        public Task<Workspace?> FindByMemberAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(workspaces.FirstOrDefault(workspace => workspace.Members.Any(member => member.UserId == userId)));
    }
}
