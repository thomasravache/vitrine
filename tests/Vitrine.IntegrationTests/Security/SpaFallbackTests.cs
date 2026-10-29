using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Rotas da SPA vs. API com um <c>index.html</c> de teste no web root (contrato da SPEC-0008): health checks anônimos
/// pelo metadado <c>AllowAnonymous</c> (não por exceção de caminho na política de fallback) e o fallback da SPA sem
/// engolir <c>/health/*</c> nem <c>/api/*</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SpaFallbackTests(PostgresFixture postgres)
{
    private static readonly string[] HealthPaths = ["/health/live", "/health/ready"];

    [Fact]
    [Trait("spec", "SPEC-0008:IT-07")]
    public async Task Health_responde_200_sem_login_e_API_inexistente_responde_404_JSON_sem_o_index_html()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();
        await using var factory = await CreateHostAsync(webRoot, cancellationToken);
        using var anonymous = new Browser(factory, TestAddresses.Client(0));

        // Dado um index.html de teste no web root (a SPA está servida)
        using (var spaRoute = await anonymous.GetAsync("/painel", cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, spaRoute, cancellationToken);
            Assert.Contains(TestWebRoot.Marker, await spaRoute.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        // Quando GET /health/live e GET /health/ready sem login, então 200 com o corpo do health (não o index.html)
        foreach (var path in HealthPaths)
        {
            using var health = await anonymous.GetAsync(path, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, health, cancellationToken);
            Assert.DoesNotContain(TestWebRoot.Marker, await health.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        // E GET /api/v1/nao-existe (e /health/nao-existe) responde 404 JSON, nunca o index.html
        foreach (var path in new[] { "/api/v1/nao-existe", "/health/nao-existe" })
        {
            using var missing = await anonymous.GetAsync(path, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NotFound, missing, cancellationToken);
            Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain(TestWebRoot.Marker, await missing.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-07")]
    public async Task Endpoints_de_health_sao_anonimos_pelo_metadado_AllowAnonymous()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var webRoot = new TestWebRoot();
        await using var factory = await CreateHostAsync(webRoot, cancellationToken);

        // Dado o host com a SPA; quando os endpoints de health são lidos do EndpointDataSource
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        // Então /health/live e /health/ready existem e carregam IAllowAnonymous (a política de fallback não os libera por caminho)
        foreach (var path in HealthPaths)
        {
            var health = endpoints.Where(endpoint => endpoint.RoutePattern.RawText == path).ToList();
            Assert.True(health.Count > 0, $"Endpoint {path} não mapeado.");
            Assert.All(health, endpoint => Assert.True(
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                $"{AnonymousEndpoints.Describe(endpoint)} sem metadado AllowAnonymous."));
        }
    }

    private async Task<ApiFactory> CreateHostAsync(TestWebRoot webRoot, CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        return IdentityHost.Create(connectionString).WithWebRoot(webRoot.Path);
    }
}
