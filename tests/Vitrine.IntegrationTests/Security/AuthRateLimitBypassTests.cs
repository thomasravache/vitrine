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
    private const int PerEmailLimit = 10;
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

    [Fact]
    [Trait("spec", "SPEC-0008:IT-03")]
    public async Task Login_com_BOM_UTF8_no_corpo_conta_no_limite_por_email_e_o_11o_recebe_429()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);

        // Dado um e-mail inexistente; quando 11 logins dele chegam de IPs diferentes com o corpo começando pelo BOM UTF-8
        var email = $"ninguem.{Guid.NewGuid():N}@example.com";
        var statuses = await LoginAttemptsAsync(factory, PerEmailLimit + 1, _ => email, bom: true, cancellationToken);

        // Então os 10 primeiros são atendidos (401) e o 11º recebe 429
        AssertLimitedOnlyAtTheLast(statuses, "login com BOM UTF-8");
    }

    [Theory]
    [Trait("spec", "SPEC-0008:IT-03")]
    [InlineData("{\"email\":")]
    [InlineData("{\"email\":\"truncado@example.com\",\"password\":\"abc")]
    [InlineData("isto não é json")]
    [InlineData("[\"email\",\"password\"]")]
    public async Task Login_com_JSON_ilegivel_responde_400(string rawBody)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);
        using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(4));

        // Dado um corpo application/json que o limitador não consegue ler; quando enviado ao login
        using var response = await client.PostContentAsync(
            IdentityApi.LoginPath, new StringContent(rawBody, Encoding.UTF8, "application/json"), cancellationToken);

        // Então 400 (falha fechada: nunca segue sem partição de e-mail)
        await IdentityApi.AssertStatusAsync(HttpStatusCode.BadRequest, response, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-03")]
    public async Task Email_com_acento_em_NFC_e_NFD_cai_na_mesma_particao_e_o_11o_login_recebe_429()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);

        // Dado um e-mail inexistente com acento, nas formas composta (NFC) e decomposta (NFD) — o mesmo e-mail para o Identity
        var local = $"jo\u00E3o.{Guid.NewGuid():N}";
        var composed = (local + "@example.com").Normalize(NormalizationForm.FormC);
        var decomposed = composed.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);

        // Quando 11 logins chegam de IPs diferentes, alternando as formas
        var statuses = await LoginAttemptsAsync(
            factory, PerEmailLimit + 1, attempt => attempt % 2 == 0 ? composed : decomposed, bom: false, cancellationToken);

        // Então somam no mesmo limite: o 11º recebe 429
        AssertLimitedOnlyAtTheLast(statuses, "login com e-mail em NFC/NFD");
    }

    /// <summary>Uma tentativa de login por IP novo, com o JSON escrito à mão em UTF-8 (caracteres sem escape).</summary>
    private static async Task<List<HttpStatusCode>> LoginAttemptsAsync(
        ApiFactory factory, int attempts, Func<int, string> emailFor, bool bom, CancellationToken cancellationToken)
    {
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var json = $$"""{"email":"{{emailFor(attempt)}}","password":"senha-errada-{{Guid.NewGuid():N}}"}""";
            byte[] bytes = bom ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)] : Encoding.UTF8.GetBytes(json);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            using var client = ProxiedClients.ViaTrustedProxy(factory, TestAddresses.Client(100 + attempt));
            using var response = await client.PostContentAsync(IdentityApi.LoginPath, content, cancellationToken);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.NotNull(response.Headers.RetryAfter);
            }
        }

        return statuses;
    }

    private static void AssertLimitedOnlyAtTheLast(List<HttpStatusCode> statuses, string what)
    {
        var described = string.Join(", ", statuses.Select(status => (int)status));
        Assert.True(
            statuses.SkipLast(1).All(status => status == HttpStatusCode.Unauthorized),
            $"{what}: as {statuses.Count - 1} primeiras tentativas deveriam ser atendidas (401): {described}");
        Assert.True(
            statuses[^1] == HttpStatusCode.TooManyRequests,
            $"{what}: a tentativa {statuses.Count} deveria receber 429: {described}");
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
