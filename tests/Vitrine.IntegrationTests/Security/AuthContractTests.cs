using System.Net;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// CT-01 (SPEC-0008): o contrato de autenticação da SPEC-0006@1 continua valendo sob as políticas novas — CSP e
/// demais cabeçalhos, rate limit com os limites do contrato e antiforgery. As chamadas seguem o fluxo do cliente
/// gerado da SPA (<c>GET /api/v1/auth/antiforgery</c> e o token no cabeçalho do POST, cookies de sessão), cada
/// navegador com seu IP; o volume fica dentro dos limites, como no uso normal.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthContractTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0008:CT-01")]
    public async Task Cadastro_e_login_mantem_os_codigos_do_contrato_201_204_401_409_423_sob_as_novas_politicas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString).WithContractRateLimits();
        var user = TestUser.New();

        // Dado o contrato SPEC-0006@1 sob as novas políticas; quando o cadastro é feito pelo fluxo da SPA
        using (var owner = new Browser(factory, TestAddresses.Client(0)))
        {
            using var register = await owner.RegisterAsync(user, cancellationToken);

            // Então 201 { userId, workspaceId } — e a resposta já sai sob a CSP
            await IdentityApi.ReadRegisteredAccountAsync(register, cancellationToken);
            Assert.True(SecurityHeaders.Values(register, "Content-Security-Policy").Count == 1, "Resposta do cadastro sem Content-Security-Policy.");
        }

        // E o mesmo e-mail de novo: 409 EMAIL_TAKEN
        using (var other = new Browser(factory, TestAddresses.Client(1)))
        {
            using var duplicate = await other.RegisterAsync(user with { Name = "Outra Pessoa" }, cancellationToken);
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Conflict, "EMAIL_TAKEN", duplicate, cancellationToken);
        }

        // E login com a senha correta: 204 com sessão (GET /me 200)
        using (var visitor = new Browser(factory, TestAddresses.Client(2)))
        {
            using var login = await visitor.LoginAsync(user.Email, user.Password, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, login, cancellationToken);
            using var me = await visitor.GetAsync(IdentityApi.MePath, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);
        }

        // E login com a senha errada: 401 INVALID_CREDENTIALS
        using (var intruder = new Browser(factory, TestAddresses.Client(3)))
        {
            using var wrong = await intruder.LoginAsync(user.Email, "senha-errada-" + Guid.NewGuid().ToString("N"), cancellationToken);
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS", wrong, cancellationToken);
        }

        // E conta bloqueada (bloqueio gravado direto no banco: 5 falhas seguidas esbarrariam no limite por e-mail
        // antes de mostrar o 423): 423 LOCKED_OUT
        await LockOutAsync(connectionString, user.Email, cancellationToken);
        using (var lockedOut = new Browser(factory, TestAddresses.Client(4)))
        {
            using var locked = await lockedOut.LoginAsync(user.Email, user.Password, cancellationToken);
            await IdentityApi.AssertProblemAsync((HttpStatusCode)StatusCodes.Status423Locked, "LOCKED_OUT", locked, cancellationToken);
        }
    }

    private static async Task LockOutAsync(string connectionString, string email, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update identity.users set lockout_enabled = true, lockout_end = now() + interval '15 minutes' where lower(email) = lower(@email)",
            connection);
        command.Parameters.AddWithValue("email", email);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }
}
