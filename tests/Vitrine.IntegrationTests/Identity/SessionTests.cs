using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

[Collection(PostgresTestGroup.Name)]
public sealed class SessionTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0006:IT-07")]
    public async Task Sem_cookie_GET_me_responde_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);

        // Dado um navegador sem sessão; quando GET /me
        using var me = await api.GetMeAsync(cancellationToken);

        // Então 401 (não um redirecionamento para página de login)
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, me, cancellationToken);
    }

    [Theory]
    [Trait("spec", "SPEC-0006:IT-07")]
    [InlineData("/health/xyz")]
    [InlineData("/health/live/extra")]
    public async Task Rota_de_health_desconhecida_sem_sessao_responde_404(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);

        // Dado um navegador sem sessão; quando GET numa rota /health desconhecida
        using var response = await api.GetAsync(path, cancellationToken);

        // Então 404 (nem 401, nem a SPA)
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NotFound, response, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-07")]
    public async Task Logout_responde_204_remove_o_cookie_e_GET_me_passa_a_responder_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);
        using var registered = await api.RegisterAsync(TestUser.New(), cancellationToken);
        await IdentityApi.ReadRegisteredAccountAsync(registered, cancellationToken);
        var sessionCookieNames = SetCookies.Session(registered).Select(cookie => cookie.Name.Value).ToHashSet(StringComparer.Ordinal);
        Assert.True(sessionCookieNames.Count > 0, "O cadastro não emitiu cookie de sessão.");

        // Dado sessão ativa
        using (var before = await api.GetMeAsync(cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, before, cancellationToken);
        }

        // Quando POST /logout
        using var logout = await api.LogoutAsync(cancellationToken);

        // Então 204 e o cookie de sessão é removido
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, logout, cancellationToken);
        var removed = SetCookies.From(logout)
            .Where(cookie => sessionCookieNames.Contains(cookie.Name.Value) && SetCookies.IsDeletion(cookie))
            .Select(cookie => cookie.Name.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            removed.SetEquals(sessionCookieNames),
            $"Logout não removeu os cookies de sessão ({string.Join(", ", sessionCookieNames)}); removidos: {string.Join(", ", removed)}.");

        // E GET /me passa a responder 401
        using var after = await api.GetMeAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, after, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-08")]
    public async Task Cada_usuario_ve_em_GET_me_apenas_o_proprio_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);

        // Dado dois usuários em workspaces distintos, cada um com sua sessão
        var userA = TestUser.New("Marina Souza");
        var userB = TestUser.New("Carlos Lima");
        using var apiA = new IdentityApi(factory);
        using var apiB = new IdentityApi(factory);
        var accountA = await apiA.RegisterSuccessfullyAsync(userA, cancellationToken);
        var accountB = await apiB.RegisterSuccessfullyAsync(userB, cancellationToken);
        Assert.NotEqual(accountA.WorkspaceId, accountB.WorkspaceId);

        // Quando cada um chama GET /me; então cada resposta contém apenas o próprio usuário e workspace
        await AssertOwnWorkspaceOnlyAsync(apiA, userA, accountA, accountB, cancellationToken);
        await AssertOwnWorkspaceOnlyAsync(apiB, userB, accountB, accountA, cancellationToken);
    }

    private static async Task AssertOwnWorkspaceOnlyAsync(
        IdentityApi api, TestUser user, RegisteredAccount own, RegisteredAccount other, CancellationToken cancellationToken)
    {
        using var me = await api.GetMeAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);
        var raw = await me.Content.ReadAsStringAsync(cancellationToken);
        var body = await IdentityApi.ReadJsonAsync(me, cancellationToken);

        Assert.Equal(own.UserId, body.GetProperty("userId").GetGuid());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.Equal(own.WorkspaceId, body.GetProperty("workspace").GetProperty("id").GetGuid());
        Assert.Equal("Owner", body.GetProperty("workspace").GetProperty("role").GetString());
        Assert.DoesNotContain(other.WorkspaceId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(other.UserId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);
    }
}
