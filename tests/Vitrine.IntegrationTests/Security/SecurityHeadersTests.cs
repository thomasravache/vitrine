using Microsoft.Extensions.Hosting;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Cabeçalhos de segurança e HTTPS estrito (contrato da SPEC-0008). SPEC-0025:IT-01 — a CSP de toda resposta traz
/// <c>img-src 'self' https://*.mlstatic.com</c> (miniaturas dos anúncios, emenda v5 da SPEC-0008), demais diretivas iguais.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SecurityHeadersTests(PostgresFixture postgres)
{
    private static readonly string[] Paths = ["/", IdentityApi.MePath];

    [Fact]
    [Trait("spec", "SPEC-0008:IT-02")]
    [Trait("spec", "SPEC-0025:IT-01")]
    public async Task Em_producao_GET_raiz_e_me_trazem_todos_os_cabecalhos_do_contrato_com_HSTS()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();

        // Dado a Api em ambiente Production simulado (SPA no web root, sem Sentry configurado), acessada em HTTPS
        await using var factory = await CreateHostAsync(webRoot, Environments.Production, cancellationToken);
        using var browser = new Browser(factory, TestAddresses.Client(0));

        foreach (var path in Paths)
        {
            // Quando GET / e GET /api/v1/me
            using var response = await browser.GetAsync(path, cancellationToken);

            // Então todos os cabeçalhos do contrato, inclusive HSTS; CSP com connect-src só 'self'; sem Server
            SecurityHeaders.AssertContract(response, expectHsts: true);
            SecurityHeaders.AssertNoServerDisclosure(response);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-02")]
    [Trait("spec", "SPEC-0025:IT-01")]
    public async Task Com_Sentry_configurado_a_CSP_libera_connect_src_so_para_a_origem_do_Sentry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();

        // Dado a Api em Production com DSN do Sentry configurado (falso, gerado por execução)
        const string SentryOrigin = "https://o0.ingest.sentry.io";
        await using var factory = (await CreateHostAsync(webRoot, Environments.Production, cancellationToken))
            .WithSetting("Sentry:Dsn", $"https://{Guid.NewGuid():N}@o0.ingest.sentry.io/0");
        using var browser = new Browser(factory, TestAddresses.Client(0));

        foreach (var path in Paths)
        {
            // Quando GET / e GET /api/v1/me
            using var response = await browser.GetAsync(path, cancellationToken);

            // Então connect-src é 'self' mais a origem do Sentry (e nada além disso)
            SecurityHeaders.AssertContract(response, expectHsts: true, sentryOrigin: SentryOrigin);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-02")]
    [Trait("spec", "SPEC-0025:IT-01")]
    public async Task Fora_de_producao_os_cabecalhos_saem_sem_HSTS()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();

        // Dado a Api em Development (HSTS é só de produção)
        await using var factory = await CreateHostAsync(webRoot, Environments.Development, cancellationToken);
        using var browser = new Browser(factory, TestAddresses.Client(0));

        foreach (var path in Paths)
        {
            // Quando GET / e GET /api/v1/me
            using var response = await browser.GetAsync(path, cancellationToken);

            // Então os demais cabeçalhos do contrato saem, e HSTS não
            SecurityHeaders.AssertContract(response, expectHsts: false);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-02")]
    [Trait("spec", "SPEC-0025:IT-01")]
    public async Task No_Kestrel_real_as_respostas_nao_trazem_Server_nem_X_Powered_By()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();

        // Dado a Api em Production servida pelo Kestrel de verdade (o TestServer não emite Server; o Kestrel emite)
        await using var factory = await CreateHostAsync(webRoot, Environments.Production, cancellationToken);
        factory.UseKestrel(0);
        factory.StartServer();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/", IdentityApi.MePath, "/health/live" })
        {
            // Quando a resposta chega pela rede (HTTP, sem proxy)
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

            // Então sem Server/X-Powered-By, e com os cabeçalhos do contrato (sem HSTS: a conexão não é HTTPS)
            SecurityHeaders.AssertNoServerDisclosure(response);
            SecurityHeaders.AssertContract(response, expectHsts: false);
        }
    }

    private async Task<ApiFactory> CreateHostAsync(TestWebRoot webRoot, string environment, CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        return IdentityHost.Create(connectionString).WithWebRoot(webRoot.Path).WithEnvironment(environment);
    }
}
