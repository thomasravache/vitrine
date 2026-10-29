using System.Net;
using System.Text;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// IT-03, regressões dos contornos achados na review G4 (contrato v4 da SPEC-0008): o limite vale para o endpoint
/// roteado (barra final e caixa diferente não escapam), o e-mail da partição é o mesmo que o endpoint usa (JSON com
/// <c>email</c> repetido é recusado) e o corpo de login/cadastro tem teto de 4 KB.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthRateLimitBypassTests(PostgresFixture postgres)
{
    private const int PerIpLimit = 10;
    private const int BodyLimitBytes = 4 * 1024;

    [Theory]
    [Trait("spec", "SPEC-0008:IT-03")]
    [InlineData("/api/v1/auth/login/")]
    [InlineData("/api/v1/auth/register/")]
    [InlineData("/API/V1/AUTH/LOGIN")]
    [InlineData("/Api/V1/Auth/Register")]
    public async Task Variacao_de_rota_do_mesmo_IP_tambem_recebe_429_no_11o_POST(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(0));

        // Token buscado anunciando outro IP: só os POST contam no IP do cliente.
        client.ExtraHeaders["X-Forwarded-For"] = TestAddresses.Client(50).ToString();
        AntiforgeryToken token;
        using (var tokenResponse = await client.GetAntiforgeryAsync(cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, tokenResponse, cancellationToken);
            token = AntiforgeryToken.From(tokenResponse);
        }

        client.ExtraHeaders["X-Forwarded-For"] = TestAddresses.Client(0).ToString();

        // Dado a Api; quando 10 POST chegam do mesmo IP pela variação da rota (corpo que nunca abre sessão: e-mail
        // inexistente no login, senha curta demais no cadastro)
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < PerIpLimit; attempt++)
        {
            using var response = await client.PostWithTokenAsync(path, Body(), token, cancellationToken);
            statuses.Add(response.StatusCode);
        }

        Assert.True(
            statuses.All(status => status is not HttpStatusCode.TooManyRequests and not HttpStatusCode.NotFound),
            $"Os 10 primeiros POST em {path} deveriam chegar ao endpoint: " + string.Join(", ", statuses.Select(status => (int)status)));

        // Então o 11º recebe 429 com Retry-After, como na rota canônica
        using var eleventh = await client.PostWithTokenAsync(path, Body(), token, cancellationToken);
        Assert.True(
            eleventh.StatusCode == HttpStatusCode.TooManyRequests,
            $"11º POST em {path} do mesmo IP: esperado 429, recebido {(int)eleventh.StatusCode}. Anteriores: "
            + string.Join(", ", statuses.Select(status => (int)status)));
        Assert.NotNull(eleventh.Headers.RetryAfter);
    }

    [Theory]
    [Trait("spec", "SPEC-0008:IT-03")]
    [InlineData("email", "Email")]
    [InlineData("email", "email")]
    [InlineData("Email", "EMAIL")]
    public async Task Login_com_email_repetido_no_JSON_responde_400(string firstKey, string secondKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        var victim = TestUser.New();
        using (var owner = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(1)))
        {
            using var register = await owner.RegisterAsync(victim, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, register, cancellationToken);
        }

        // Dado um JSON de login com a chave do e-mail repetida (um e-mail descartável e o da vítima)
        var decoy = $"isca.{Guid.NewGuid():N}@example.com";
        var password = "senha-errada-" + Guid.NewGuid().ToString("N");
        var json = $$"""{"{{firstKey}}":"{{decoy}}","{{secondKey}}":"{{victim.Email}}","password":"{{password}}"}""";

        // Quando enviado
        using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(2));
        using var response = await client.PostContentAsync(
            IdentityApi.LoginPath, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        // Então 400: o limite por e-mail não pode partir de um e-mail e o login usar outro
        await IdentityApi.AssertStatusAsync(HttpStatusCode.BadRequest, response, cancellationToken);
    }

    [Theory]
    [Trait("spec", "SPEC-0008:IT-03")]
    [InlineData("/api/v1/auth/login", false)]
    [InlineData("/api/v1/auth/login", true)]
    [InlineData("/api/v1/auth/register", false)]
    [InlineData("/api/v1/auth/register", true)]
    public async Task Corpo_de_login_ou_cadastro_acima_de_4_KB_responde_413(string path, bool chunked)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(3));

        // Dado um JSON válido acima de 4 KB (com Content-Length, ou em chunks sem tamanho declarado)
        var padding = new string('a', BodyLimitBytes + 512);
        var json = $$"""{"name":"Marina Souza","email":"grande.{{Guid.NewGuid():N}}@example.com","password":"{{padding}}"}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        HttpContent content = chunked
            ? new StreamContent(new UnknownLengthStream(bytes))
            : new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        // Quando enviado
        using var response = await client.PostContentAsync(path, content, cancellationToken);

        // Então 413
        await IdentityApi.AssertStatusAsync(HttpStatusCode.RequestEntityTooLarge, response, cancellationToken);
    }

    private static object Body() => new
    {
        name = "Marina Souza",
        email = $"ninguem.{Guid.NewGuid():N}@example.com",
        password = "curta-" + Guid.NewGuid().ToString("N")[..2],
    };

    private async Task<ApiFactory> CreateHostAsync(CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        return IdentityHost.Create(connectionString).TrustingTestProxy();
    }

    /// <summary>Corpo sem tamanho conhecido: o cliente envia em chunks, sem Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
