using System.Net;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security.Support;

/// <summary>
/// Clientes que chegam à Api como em produção: a conexão vem do proxy (Caddy) na rede confiável e o IP real do
/// cliente vem em <c>X-Forwarded-For</c> (com <c>X-Forwarded-Proto: https</c>). O host confia só em
/// <see cref="TestAddresses.TrustedProxyNetwork"/> (<see cref="SecuritySettings.ForwardedKnownNetwork"/>).
/// </summary>
internal static class ProxiedClients
{
    public static ApiFactory TrustingTestProxy(this ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithSetting(SecuritySettings.ForwardedKnownNetwork, TestAddresses.TrustedProxyNetwork);
    }

    /// <summary>Navegador do cliente <paramref name="clientIp"/> atrás do proxy confiável.</summary>
    public static Browser ViaTrustedProxy(ApiFactory factory, IPAddress clientIp, Uri? origin = null) =>
        ViaProxy(factory, TestAddresses.TrustedProxy, clientIp, origin);

    /// <summary>Navegador cuja conexão vem de <paramref name="peer"/> anunciando o cliente <paramref name="clientIp"/>.</summary>
    public static Browser ViaProxy(ApiFactory factory, IPAddress peer, IPAddress clientIp, Uri? origin = null)
    {
        ArgumentNullException.ThrowIfNull(clientIp);
        var browser = new Browser(factory, peer, origin);
        browser.ExtraHeaders["X-Forwarded-For"] = clientIp.ToString();
        browser.ExtraHeaders["X-Forwarded-Proto"] = "https";
        return browser;
    }
}
