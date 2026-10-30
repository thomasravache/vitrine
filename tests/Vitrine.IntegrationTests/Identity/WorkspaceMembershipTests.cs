using Microsoft.Extensions.DependencyInjection;
using Vitrine.Identity.PublicApi;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

/// <summary><c>IWorkspaceMembership</c> do <c>Identity.PublicApi</c> no host real (SPEC-0020).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class WorkspaceMembershipTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0020:IT-10")]
    public async Task GetAsync_devolve_o_workspace_do_cadastro_e_Owner_para_o_recem_cadastrado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);

        // Dado Postgres real e um Owner recém-cadastrado
        var account = await api.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);

        // Quando IWorkspaceMembership.GetAsync é chamado (como um módulo faria, num escopo de requisição)
        await using var scope = factory.Services.CreateAsyncScope();
        var membership = scope.ServiceProvider.GetRequiredService<IWorkspaceMembership>();
        var result = await membership.GetAsync(account.UserId, cancellationToken);

        // Então devolve o workspace criado no cadastro e o papel Owner
        Assert.Equal(new WorkspaceMembership(account.WorkspaceId, "Owner"), result);

        // E null para um usuário sem workspace
        Assert.Null(await membership.GetAsync(Guid.NewGuid(), cancellationToken));
    }
}
