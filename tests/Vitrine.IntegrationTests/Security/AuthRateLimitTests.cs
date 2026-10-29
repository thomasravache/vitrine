using System.Net;
using Microsoft.AspNetCore.Http;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Rate limiting de auth (contrato v3 da SPEC-0008), com os limites reais do contrato: 10 POST de cadastro/login por
/// minuto por IP (GET /antiforgery e logout não contam) e 10 logins/min por e-mail, com 429 + <c>Retry-After</c>; o bloqueio da SPEC-0006 (5 falhas → 423) vem antes
/// do limite por e-mail. Os IPs são simulados como em produção: a conexão TCP vem do proxy confiável
/// (IP gravado em <c>RemoteIpAddress</c> pelo <see cref="TestRemoteIpStartupFilter"/>) e o IP do cliente vem em
/// <c>X-Forwarded-For</c> — então o limite por IP só funciona se a Api particionar pelo IP real do cliente (depois
/// dos forwarded headers), e não pelo IP do Caddy, que é o mesmo para todos.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthRateLimitTests(PostgresFixture postgres)
{
    private const int PerIpLimit = 10;
    private const int PerEmailLimit = 10;
    private const int MaxFailedAttempts = 5;

    [Fact]
    [Trait("spec", "SPEC-0008:IT-03")]
    public async Task O_11o_POST_de_cadastro_ou_login_do_mesmo_IP_no_minuto_recebe_429_com_Retry_After()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(0));

        // O token antiforgery é buscado uma vez, anunciando outro IP: só os POST contam no IP do cliente, seja qual
        // for a regra do GET. Logins que falham não mudam a identidade, então o mesmo token vale para todos os POST.
        client.ExtraHeaders["X-Forwarded-For"] = TestAddresses.Client(50).ToString();
        AntiforgeryToken token;
        using (var tokenResponse = await client.GetAntiforgeryAsync(cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, tokenResponse, cancellationToken);
            token = AntiforgeryToken.From(tokenResponse);
        }

        client.ExtraHeaders["X-Forwarded-For"] = TestAddresses.Client(0).ToString();

        // Dado a Api; quando 10 POST de login chegam do mesmo IP no minuto (e-mails inexistentes distintos: 401)
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < PerIpLimit; attempt++)
        {
            using var login = await client.PostWithTokenAsync(
                IdentityApi.LoginPath, new { email = UnknownEmail(), password = "senha-qualquer-" + Guid.NewGuid().ToString("N") }, token, cancellationToken);
            statuses.Add(login.StatusCode);
        }

        Assert.True(
            statuses.All(status => status == HttpStatusCode.Unauthorized),
            "Os 10 primeiros POST de auth do IP deveriam ser atendidos (401): " + string.Join(", ", statuses.Select(status => (int)status)));

        // Então o 11º POST — de cadastro, que divide o limite com o login — recebe 429 com Retry-After
        using var eleventh = await client.PostWithTokenAsync(IdentityApi.RegisterPath, IdentityApi.RegisterBody(TestUser.New()), token, cancellationToken);
        AssertTooManyRequests(eleventh);

        // E outro IP (mesmo proxy) continua sendo atendido: o limite é por IP do cliente, não global nem do proxy
        using var otherClient = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(1));
        using var other = await otherClient.LoginAsync(UnknownEmail(), "senha-qualquer-" + Guid.NewGuid().ToString("N"), cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, other, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-03")]
    public async Task GET_antiforgery_do_mesmo_IP_nao_conta_no_limite_e_segue_204_alem_de_10_por_minuto()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(2));

        // Dado a Api; quando 15 GET /antiforgery chegam do mesmo IP no minuto (ex.: vários usuários atrás de um CGNAT)
        var statuses = new List<HttpStatusCode>();
        for (var request = 0; request < PerIpLimit + 5; request++)
        {
            using var response = await client.GetAntiforgeryAsync(cancellationToken);
            statuses.Add(response.StatusCode);
        }

        // Então todos 204: o GET do token não é limitado
        Assert.True(
            statuses.All(status => status == HttpStatusCode.NoContent),
            "GET /api/v1/auth/antiforgery não deveria contar no limite por IP: " + string.Join(", ", statuses.Select(status => (int)status)));
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-03")]
    public async Task O_11o_login_do_mesmo_email_inexistente_no_minuto_vindo_de_IPs_diferentes_recebe_429()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);

        // Dado um e-mail (inexistente: nenhum bloqueio de conta interfere — todo login dele é 401)
        var email = UnknownEmail();

        // Quando 10 tentativas de login desse e-mail chegam, cada uma de um IP diferente
        for (var attempt = 0; attempt < PerEmailLimit; attempt++)
        {
            using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(10 + attempt));
            using var login = await client.LoginAsync(email, "senha-errada-" + Guid.NewGuid().ToString("N"), cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, login, cancellationToken);
        }

        // Então a 11ª, de mais um IP novo, recebe 429 com Retry-After
        using (var sixthClient = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(10 + PerEmailLimit)))
        {
            using var sixth = await sixthClient.LoginAsync(email, "senha-errada-" + Guid.NewGuid().ToString("N"), cancellationToken);
            AssertTooManyRequests(sixth);
        }

        // E outro e-mail continua sendo atendido (o limite é por e-mail)
        using var otherClient = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(30));
        using var other = await otherClient.LoginAsync(UnknownEmail(), "senha-errada-" + Guid.NewGuid().ToString("N"), cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, other, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-03")]
    public async Task Para_email_existente_a_6a_tentativa_apos_5_falhas_continua_423_e_nao_429()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        var user = TestUser.New();
        using (var owner = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(40)))
        {
            using var register = await owner.RegisterAsync(user, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, register, cancellationToken);
        }

        // Dado um e-mail existente com 5 falhas seguidas de login (cada uma de um IP; a 5ª já pode responder 423)
        for (var attempt = 0; attempt < MaxFailedAttempts; attempt++)
        {
            using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(41 + attempt));
            using var failed = await client.LoginAsync(user.Email, "senha-errada-" + Guid.NewGuid().ToString("N"), cancellationToken);
            Assert.True(
                failed.StatusCode is HttpStatusCode.Unauthorized or (HttpStatusCode)StatusCodes.Status423Locked,
                $"Falha {attempt + 1} de login respondeu {(int)failed.StatusCode}; esperado 401 ou 423.");
        }

        // Quando a 6ª tentativa chega (mesmo com a senha correta), sob os limites reais do contrato
        using var sixthClient = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(41 + MaxFailedAttempts));
        using var sixth = await sixthClient.LoginAsync(user.Email, user.Password, cancellationToken);

        // Então 423 LOCKED_OUT (contrato da SPEC-0006), não 429
        await IdentityApi.AssertProblemAsync((HttpStatusCode)StatusCodes.Status423Locked, "LOCKED_OUT", sixth, cancellationToken);
    }

    private static string UnknownEmail() => $"ninguem.{Guid.NewGuid():N}@example.com";

    private static void AssertTooManyRequests(HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.TooManyRequests,
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: esperado 429, recebido {(int)response.StatusCode}.");
        var retryAfter = response.Headers.RetryAfter;
        Assert.True(retryAfter is not null, "429 sem cabeçalho Retry-After.");
        var delay = retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow);
        Assert.True(
            delay > TimeSpan.Zero && delay <= TimeSpan.FromMinutes(1),
            $"Retry-After deveria apontar para dentro da janela de 1 minuto; veio \"{retryAfter}\".");
    }

    private async Task<ApiFactory> CreateHostAsync(CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        return IdentityHost.Create(connectionString).TrustingTestProxy();
    }
}
