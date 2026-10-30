using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

/// <summary>
/// CH-01 da SPEC-0020: caracteriza o comportamento dos filtros <c>AntiforgeryFilter</c> e <c>HttpsRequiredFilter</c>
/// do Identity e a lista de endpoints anônimos do host <b>antes</b> da extração dos filtros para
/// <c>Vitrine.Common.Web</c>. Passa no código atual e tem de continuar passando, sem alteração, depois do refactor.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthFiltersCharacterizationTests(PostgresFixture postgres)
{
    /// <summary>
    /// Endpoints anônimos do host sem a SPA no web root, como "MÉTODO padrão" (ordenados). A SPEC-0020 não cria
    /// nenhum endpoint anônimo: a conclusão do OAuth é um POST autenticado (seção 4 da spec).
    /// </summary>
    private static readonly string[] AnonymousToday =
    [
        "* /api/{**path}",
        "* /health/{**path}",
        "GET /api/v1/auth/antiforgery",
        "GET /health/live",
        "GET /health/ready",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/register",
    ];

    [Fact]
    [Trait("spec", "SPEC-0020:CH-01")]
    public async Task Endpoints_anonimos_do_host_sao_exatamente_os_de_hoje()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);

        // Dado o host real (sem SPA no web root); quando os endpoints anônimos são listados
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);
        var anonymous = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(AnonymousEndpoints.Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Então são exatamente os de hoje (nada novo, nada a menos)
        Assert.Equal(AnonymousToday, anonymous);

        // E a regra de autorização por padrão continua sem violações
        var violations = await AnonymousEndpoints.ViolationsAsync(endpoints, factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());
        Assert.True(violations.Count == 0, "Endpoints anônimos fora da lista AnonymousEndpoints: " + string.Join("; ", violations));
    }

    [Theory]
    [Trait("spec", "SPEC-0020:CH-01")]
    [InlineData(IdentityApi.RegisterPath)]
    [InlineData(IdentityApi.LoginPath)]
    [InlineData(IdentityApi.LogoutPath)]
    public async Task POST_de_auth_sem_token_antiforgery_responde_400_ANTIFORGERY_INVALID(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);
        var user = TestUser.New();
        if (path == IdentityApi.LogoutPath)
        {
            // Logout exige sessão: sem ela a resposta seria 401 antes do filtro.
            await api.RegisterSuccessfullyAsync(user, cancellationToken);
        }

        // Dado o cookie antiforgery no cliente, mas sem o cabeçalho com o token; quando o POST é enviado em HTTPS
        _ = await api.GetAntiforgeryTokenAsync(cancellationToken);
        object? body = path switch
        {
            IdentityApi.RegisterPath => IdentityApi.RegisterBody(TestUser.New()),
            IdentityApi.LoginPath => new { email = user.Email, password = user.Password },
            _ => null,
        };
        using var response = await api.PostWithTokenAsync(path, body, token: null, cancellationToken);

        // Então o filtro antiforgery responde 400 problem+json com code ANTIFORGERY_INVALID
        await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "ANTIFORGERY_INVALID", response, cancellationToken);
    }

    [Theory]
    [Trait("spec", "SPEC-0020:CH-01")]
    [InlineData("GET", IdentityApi.AntiforgeryPath)]
    [InlineData("POST", IdentityApi.RegisterPath)]
    [InlineData("POST", IdentityApi.LoginPath)]
    public async Task Em_producao_auth_em_HTTP_puro_responde_400_BAD_REQUEST_antes_do_antiforgery(string method, string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado a Api em Production (cookie antiforgery Secure=Always), sem proxy confiável
        await using var factory = IdentityHost.Create(connectionString).WithEnvironment(Environments.Production);
        using var browser = new Browser(factory, TestAddresses.Client(0), Browser.HttpOrigin);

        // Quando a rota de auth chega em HTTP puro (sem token antiforgery)
        using var response = await browser.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative))
            {
                // Corpo válido: o limitador de login (SPEC-0014) recusa antes o login sem e-mail.
                Content = method == "POST" ? JsonContent.Create(IdentityApi.RegisterBody(TestUser.New())) : null,
            },
            cancellationToken);

        // Então o filtro de HTTPS responde 400 com o code genérico (não ANTIFORGERY_INVALID) e título "HTTPS obrigatório."
        var problem = await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "BAD_REQUEST", response, cancellationToken);
        Assert.Equal("HTTPS obrigatório.", problem.GetProperty("title").GetString());
    }

    [Fact]
    [Trait("spec", "SPEC-0020:CH-01")]
    public async Task Fora_de_producao_GET_antiforgery_em_HTTP_responde_204()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado a Api em Development (cookie antiforgery segue a requisição); quando GET /antiforgery chega em HTTP
        await using var factory = IdentityHost.Create(connectionString).WithEnvironment(Environments.Development);
        using var browser = new Browser(factory, TestAddresses.Client(0), Browser.HttpOrigin);
        using var response = await browser.GetAntiforgeryAsync(cancellationToken);

        // Então o filtro de HTTPS deixa passar
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, response, cancellationToken);
    }
}
