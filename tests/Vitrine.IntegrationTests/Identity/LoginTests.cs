using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

[Collection(PostgresTestGroup.Name)]
public sealed class LoginTests(PostgresFixture postgres)
{
    // Identificadores de correlação variam por requisição por natureza; o resto do corpo precisa ser idêntico.
    private static readonly string[] PerRequestFields = ["traceId", "requestId"];

    [Fact]
    [Trait("spec", "SPEC-0006:IT-03")]
    public async Task Login_com_senha_correta_responde_204_com_cookie_HttpOnly_Secure_SameSite_Lax()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var user = await RegisterAsync(factory, cancellationToken);

        // Dado um usuário existente; quando POST /login com a senha correta, num navegador sem sessão
        using var api = new IdentityApi(factory);
        using var response = await api.LoginAsync(user.Email, user.Password, cancellationToken);

        // Então 204 + cookie de sessão HttpOnly; Secure; SameSite=Lax
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, response, cancellationToken);
        var sessionCookies = SetCookies.Session(response);
        Assert.True(sessionCookies.Count > 0, "O login não emitiu cookie de sessão.");
        Assert.All(sessionCookies, cookie =>
        {
            Assert.True(cookie.HttpOnly, $"Cookie de sessão {cookie.Name} sem HttpOnly.");
            Assert.True(cookie.Secure, $"Cookie de sessão {cookie.Name} sem Secure.");
            Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        });

        // E a sessão dá acesso ao painel (GET /me)
        using var me = await api.GetMeAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-03")]
    public async Task Senha_errada_e_email_inexistente_respondem_401_INVALID_CREDENTIALS_com_corpo_identico()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var user = await RegisterAsync(factory, cancellationToken);
        using var api = new IdentityApi(factory);

        // Dado um usuário existente; quando POST /login com senha errada e com e-mail inexistente
        using var wrongPassword = await api.LoginAsync(user.Email, "senha-errada-123", cancellationToken);
        using var unknownEmail = await api.LoginAsync($"inexistente.{Guid.NewGuid():N}@example.com", user.Password, cancellationToken);

        // Então ambos 401 INVALID_CREDENTIALS, sem sessão
        var wrongPasswordBody = await IdentityApi.AssertProblemAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS", wrongPassword, cancellationToken);
        var unknownEmailBody = await IdentityApi.AssertProblemAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS", unknownEmail, cancellationToken);
        Assert.Empty(SetCookies.Session(wrongPassword));
        Assert.Empty(SetCookies.Session(unknownEmail));

        // E com corpo idêntico (não revela se o e-mail existe)
        Assert.Equal(Normalize(wrongPasswordBody), Normalize(unknownEmailBody));
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-04")]
    public async Task Apos_5_falhas_seguidas_o_6o_login_responde_423_LOCKED_OUT_com_retryAfterSeconds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var user = await RegisterAsync(factory, cancellationToken);
        using var api = new IdentityApi(factory);

        // Dado um usuário existente; quando 5 logins falham seguidos
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var failed = await api.LoginAsync(user.Email, $"senha-errada-{attempt:00}", cancellationToken);
            if (attempt < 5)
            {
                await IdentityApi.AssertProblemAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS", failed, cancellationToken);
            }
            else
            {
                // A 5ª falha é a que bloqueia: pode já responder 401 ou 423, nunca sucesso.
                Assert.True(
                    failed.StatusCode is HttpStatusCode.Unauthorized or (HttpStatusCode)StatusCodes.Status423Locked,
                    $"5ª tentativa errada respondeu {(int)failed.StatusCode}.");
            }
        }

        // Então o 6º — mesmo com a senha correta — responde 423 LOCKED_OUT com retryAfterSeconds > 0 (até 15 min)
        using var locked = await api.LoginAsync(user.Email, user.Password, cancellationToken);
        var problem = await IdentityApi.AssertProblemAsync((HttpStatusCode)StatusCodes.Status423Locked, "LOCKED_OUT", locked, cancellationToken);
        Assert.True(problem.TryGetProperty("retryAfterSeconds", out var retryAfter), $"Problem sem retryAfterSeconds: {problem}");
        Assert.InRange(retryAfter.GetDouble(), double.Epsilon, TimeSpan.FromMinutes(15).TotalSeconds);
        Assert.Empty(SetCookies.Session(locked));

        // E nenhuma sessão foi aberta
        using var me = await api.GetMeAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, me, cancellationToken);
    }

    private static async Task<TestUser> RegisterAsync(ApiFactory factory, CancellationToken cancellationToken)
    {
        var user = TestUser.New();
        using var api = new IdentityApi(factory);
        await api.RegisterSuccessfullyAsync(user, cancellationToken);
        return user;
    }

    private static string Normalize(JsonElement problem)
    {
        var node = JsonNode.Parse(problem.GetRawText())!.AsObject();
        foreach (var field in PerRequestFields)
        {
            node.Remove(field);
        }

        return node.ToJsonString();
    }
}
