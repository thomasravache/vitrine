using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Autorização negada por padrão (ADR-0007, SPEC-0008). UT-01 é a regra de arquitetura sobre os endpoints do host
/// real (<see cref="EndpointDataSource"/>); fica no projeto de integração porque o host da Api (com Wolverine sobre o
/// PostgreSQL) só sobe com o banco do teste. O host sobe com a SPA no web root, para os fallbacks dela entrarem na regra.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthorizationByDefaultTests(PostgresFixture postgres)
{
    private const string SamplePattern = "/api/v1/amostra-sem-politica";

    [Fact]
    [Trait("spec", "SPEC-0008:UT-01")]
    public async Task Politica_de_fallback_e_RequireAuthenticatedUser_sem_excecao_por_caminho()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();
        await using var factory = await CreateHostAsync(webRoot, cancellationToken);

        // Dado o host real; quando a política de fallback é lida
        var fallback = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        // Então ela é exatamente "usuário autenticado" — nenhuma asserção que libere caminhos (ex.: /health)
        Assert.True(
            AnonymousEndpoints.IsRequireAuthenticatedUser(fallback),
            "FallbackPolicy deveria ser RequireAuthenticatedUser; requisitos atuais: "
            + (fallback is null ? "(nenhuma)" : string.Join(", ", fallback.Requirements.Select(requirement => requirement.GetType().Name))));
    }

    [Fact]
    [Trait("spec", "SPEC-0008:UT-01")]
    public async Task Endpoints_do_host_so_sao_anonimos_se_estiverem_na_lista()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();
        await using var factory = await CreateHostAsync(webRoot, cancellationToken);

        // Dado a lista de endpoints do host (sem rota de amostra)
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.NotEmpty(endpoints);

        // Quando a regra "autorização por padrão" roda
        var violations = await AnonymousEndpoints.ViolationsAsync(endpoints, factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        // Então passa: nenhum endpoint anônimo fora da lista
        Assert.True(violations.Count == 0, "Endpoints anônimos fora da lista AnonymousEndpoints: " + string.Join("; ", violations));
    }

    [Fact]
    [Trait("spec", "SPEC-0008:UT-01")]
    public async Task Rota_de_amostra_anonima_fora_da_lista_faz_a_regra_falhar_apontando_a_rota()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();
        await using var factory = await CreateHostAsync(webRoot, cancellationToken);

        // Dado os endpoints do host e uma rota de amostra que escapa da política (AllowAnonymous fora da lista)
        var sample = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(SamplePattern), order: 0)
        {
            Metadata = { new HttpMethodMetadata(["GET"]), new AllowAnonymousAttribute() },
        }.Build();
        IEnumerable<Endpoint> endpoints = [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints, sample];

        // Quando a regra roda
        var violations = await AnonymousEndpoints.ViolationsAsync(endpoints, factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        // Então falha apontando a rota de amostra — e só ela
        Assert.Equal([$"GET {SamplePattern}"], violations);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-01")]
    public async Task Rotas_protegidas_sem_cookie_respondem_401_e_as_da_lista_anonima_respondem_sem_login()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();
        await using var factory = await CreateHostAsync(webRoot, cancellationToken);

        // Dado a Api; quando cada rota protegida conhecida (endpoints fora da lista, sem parâmetros) é chamada sem cookie
        var protectedRoutes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => !AnonymousEndpoints.IsListedPattern(endpoint) && endpoint.RoutePattern.Parameters.Count == 0)
            .ToList();
        Assert.Contains(protectedRoutes, endpoint => endpoint.RoutePattern.RawText == IdentityApi.MePath);
        Assert.Contains(protectedRoutes, endpoint => endpoint.RoutePattern.RawText == IdentityApi.LogoutPath);

        using var anonymous = new Browser(factory, TestAddresses.Client(0));
        foreach (var endpoint in protectedRoutes)
        {
            var method = AnonymousEndpoints.Methods(endpoint)?[0] ?? HttpMethods.Get;
            using var response = await anonymous.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), new Uri(endpoint.RoutePattern.RawText!, UriKind.Relative)),
                cancellationToken);

            // Então 401 (sem redirecionar para tela de login)
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{AnonymousEndpoints.Describe(endpoint)} sem cookie respondeu {(int)response.StatusCode}; esperado 401.");
        }

        // E cada rota da lista anônima responde sem exigir login
        var user = TestUser.New();
        using (var antiforgery = await anonymous.GetAntiforgeryAsync(cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, antiforgery, cancellationToken);
        }

        using (var register = await anonymous.RegisterAsync(user, cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, register, cancellationToken);
        }

        using var visitor = new Browser(factory, TestAddresses.Client(1));
        using (var login = await visitor.LoginAsync(user.Email, user.Password, cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, login, cancellationToken);
        }

        using var stranger = new Browser(factory, TestAddresses.Client(2));
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await stranger.GetAsync(path, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, health, cancellationToken);
        }

        foreach (var path in new[] { "/", "/painel" })
        {
            using var spa = await stranger.GetAsync(path, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, spa, cancellationToken);
            Assert.Contains(TestWebRoot.Marker, await spa.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }
    }

    private async Task<ApiFactory> CreateHostAsync(TestWebRoot webRoot, CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        return IdentityHost.Create(connectionString).WithWebRoot(webRoot.Path);
    }
}
