using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// SPEC-0023:CH-01 — caracterização, antes do webhook de notificações do ML, da lista <c>AnonymousEndpoints</c> e do
/// rate limit por IP de auth (SPEC-0008). Passa no código atual e continua passando depois da mudança: a única entrada
/// nova permitida na lista é <c>POST /api/v1/mercadolivre/notifications</c>, e o limite por IP de cadastro/login segue
/// 10/min, sem ser consumido por POST na rota do webhook.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AnonymousEndpointsCharacterizationTests(PostgresFixture postgres)
{
    private const int AuthPerIpLimit = 10;
    private const string WebhookPath = "/api/v1/mercadolivre/notifications";

    /// <summary>A lista do contrato da SPEC-0008 antes da SPEC-0023 (ordem e conteúdo).</summary>
    private static readonly AnonymousEndpoints.Entry[] Baseline =
    [
        new("POST", "/api/v1/auth/register"),
        new("POST", "/api/v1/auth/login"),
        new("GET", "/api/v1/auth/antiforgery"),
        new("GET", "/health/live"),
        new("GET", "/health/ready"),
        new(null, "{*path:nonfile}"),
        new(null, "/api/{**path}"),
        new(null, "/health/{**path}"),
    ];

    private static readonly AnonymousEndpoints.Entry WebhookEntry = new("POST", WebhookPath);

    [Fact]
    [Trait("spec", "SPEC-0023:CH-01")]
    public async Task Lista_AnonymousEndpoints_so_ganha_o_webhook_e_a_regra_de_autorizacao_por_padrao_passa()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a lista AnonymousEndpoints: fora a entrada do webhook (emenda v5 da SPEC-0008), é a lista atual
        var others = AnonymousEndpoints.Allowed.Where(entry => entry != WebhookEntry).ToList();
        Assert.Equal(Baseline, others);
        Assert.True(
            AnonymousEndpoints.Allowed.Count(entry => entry == WebhookEntry) <= 1,
            "A entrada do webhook aparece mais de uma vez na lista AnonymousEndpoints.");

        // Quando a regra "autorização por padrão" (SPEC-0008:UT-01) roda sobre o host real, com a SPA no web root
        using var webRoot = new TestWebRoot();
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString).WithWebRoot(webRoot.Path);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var violations = await AnonymousEndpoints.ViolationsAsync(endpoints, factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        // Então passa: nenhum endpoint anônimo fora da lista
        Assert.True(violations.Count == 0, "Endpoints anônimos fora da lista AnonymousEndpoints: " + string.Join("; ", violations));
    }

    [Fact]
    [Trait("spec", "SPEC-0023:CH-01")]
    public async Task Limite_por_IP_de_auth_segue_10_por_minuto_e_nao_e_consumido_por_POST_na_rota_do_webhook()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var ip = TestAddresses.Client(70);

        // Dado 15 POST na rota do webhook vindos do IP (application_id de outro app: nada é gravado depois da mudança)
        using (var raw = factory.CreateDefaultClient(Browser.HttpsOrigin, new TestRemoteIpHandler(ip)))
        {
            for (var request = 0; request < 15; request++)
            {
                using var response = await raw.PostAsJsonAsync(
                    WebhookPath,
                    new
                    {
                        _id = Guid.NewGuid().ToString(),
                        resource = "/items/MLB1",
                        user_id = 1L,
                        topic = "items",
                        application_id = 1L,
                        attempts = 1,
                        sent = "2026-10-30T12:00:00.000Z",
                        received = "2026-10-30T12:00:00.000Z",
                    },
                    cancellationToken);
                Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            }
        }

        // Quando 10 POST de login (e-mails inexistentes distintos) chegam do mesmo IP no minuto
        using var browser = new Browser(factory, ip);
        for (var attempt = 0; attempt < AuthPerIpLimit; attempt++)
        {
            using var login = await browser.LoginAsync(UnknownEmail(), "senha-qualquer-" + Guid.NewGuid().ToString("N"), cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, login, cancellationToken);
        }

        // Então o 11º recebe 429 com Retry-After (o limite de auth é o de antes, e os POST do webhook não contaram nele)
        using var eleventh = await browser.LoginAsync(UnknownEmail(), "senha-qualquer-" + Guid.NewGuid().ToString("N"), cancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        Assert.NotNull(eleventh.Headers.RetryAfter);

        // E outro IP segue atendido
        using var other = new Browser(factory, TestAddresses.Client(71));
        using var otherLogin = await other.LoginAsync(UnknownEmail(), "senha-qualquer-" + Guid.NewGuid().ToString("N"), cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, otherLogin, cancellationToken);
    }

    private static string UnknownEmail() => $"ninguem-{Guid.NewGuid():N}@vitrine.test";
}
