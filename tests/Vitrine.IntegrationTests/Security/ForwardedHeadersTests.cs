using System.Net;
using Microsoft.Extensions.Hosting;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Api atrás do Caddy (contrato da SPEC-0008): X-Forwarded-Proto/For só valem vindos da rede do proxy. A conexão
/// chega em HTTP ao Kestrel (como do Caddy na rede do Compose); o IP de origem dela é simulado pelo
/// <see cref="TestRemoteIpStartupFilter"/>. <c>Request.IsHttps</c> é observado pelo cookie antiforgery
/// (<c>Secure=Always</c>) e pelo HSTS, que o ASP.NET Core só emite em requisições HTTPS.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ForwardedHeadersTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0008:IT-06")]
    public async Task X_Forwarded_Proto_https_vindo_do_proxy_confiavel_faz_o_cookie_antiforgery_sair_Secure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);

        // Dado a Api configurada para confiar só na rede do proxy; quando GET /antiforgery chega em HTTP vindo do
        // proxy confiável com X-Forwarded-Proto: https
        using var proxied = ProxiedClients.ViaProxy(factory, TestAddresses.TrustedProxy, TestAddresses.Client(0), Browser.HttpOrigin);
        using var response = await proxied.GetAntiforgeryAsync(cancellationToken);

        // Então 204 e o cookie antiforgery sai com Secure (a Api enxerga HTTPS: HSTS também sai)
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, response, cancellationToken);
        var antiforgeryCookies = SetCookies.From(response).Where(cookie => !SetCookies.Session(response).Contains(cookie)).ToList();
        Assert.True(antiforgeryCookies.Count > 0, "GET /api/v1/auth/antiforgery não emitiu o cookie antiforgery.");
        Assert.All(antiforgeryCookies, cookie => Assert.True(cookie.Secure, $"Cookie antiforgery {cookie.Name} sem Secure atrás do proxy."));
        Assert.True(SecurityHeaders.Values(response, "Strict-Transport-Security").Count == 1, "Requisição HTTPS encaminhada pelo proxy deveria receber HSTS.");
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-06")]
    public async Task X_Forwarded_Proto_vindo_de_IP_nao_confiavel_e_ignorado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await CreateHostAsync(cancellationToken);

        // Dado a mesma Api; quando o mesmo GET com X-Forwarded-Proto: https chega de um IP fora da rede do proxy
        using var direct = ProxiedClients.ViaProxy(factory, TestAddresses.UntrustedPeer, TestAddresses.Client(0), Browser.HttpOrigin);
        using var response = await direct.GetAntiforgeryAsync(cancellationToken);

        // Então o cabeçalho é ignorado: a requisição continua HTTP — sem HSTS e sem cookie antiforgery Secure emitido
        // (com Secure=Always o antiforgery não emite cookie em HTTP)
        Assert.True(
            SecurityHeaders.Values(response, "Strict-Transport-Security").Count == 0,
            "X-Forwarded-Proto de IP não confiável foi aceito: a resposta saiu com HSTS.");
        Assert.DoesNotContain(SetCookies.From(response), cookie => cookie.Secure && !SetCookies.Session(response).Contains(cookie));
    }

    private async Task<ApiFactory> CreateHostAsync(CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        return IdentityHost.Create(connectionString).WithEnvironment(Environments.Production).TrustingTestProxy();
    }
}
