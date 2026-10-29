using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security.Support;

/// <summary>
/// Um navegador (ou o proxy em nome dele) contra a Api em memória: cookies mantidos, sem seguir redirecionamentos,
/// conexão vinda de <paramref name="remoteIp"/> (<see cref="TestRemoteIpHandler"/>) e o fluxo antiforgery do contrato
/// SPEC-0006@1 — o mesmo do cliente gerado da SPA: <c>GET /api/v1/auth/antiforgery</c> e o token no cabeçalho do POST.
/// </summary>
internal sealed class Browser : IDisposable
{
    /// <summary>Host público (o localhost é excluído do HSTS pelo ASP.NET Core).</summary>
    public static readonly Uri HttpsOrigin = new("https://app.vitrine.test");

    /// <summary>Mesmo host, em HTTP: a conexão do Caddy com o Kestrel na rede do Compose.</summary>
    public static readonly Uri HttpOrigin = new("http://app.vitrine.test");

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client;

    public Browser(ApiFactory factory, IPAddress remoteIp, Uri? origin = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        RemoteIp = remoteIp;
        _client = factory.CreateDefaultClient(origin ?? HttpsOrigin, new CookieContainerHandler(Cookies), new TestRemoteIpHandler(remoteIp));
    }

    public IPAddress RemoteIp { get; }

    public CookieContainer Cookies { get; } = new();

    /// <summary>Cabeçalhos acrescentados a toda requisição (ex.: X-Forwarded-Proto do proxy).</summary>
    public Dictionary<string, string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative)), cancellationToken);

    /// <summary><c>GET /api/v1/auth/antiforgery</c> (resposta crua).</summary>
    public Task<HttpResponseMessage> GetAntiforgeryAsync(CancellationToken cancellationToken) =>
        GetAsync(IdentityApi.AntiforgeryPath, cancellationToken);

    /// <summary>POST com token antiforgery recém-obtido; se o GET do token não der 204, devolve a resposta dele.</summary>
    public async Task<HttpResponseMessage> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        var tokenResponse = await GetAntiforgeryAsync(cancellationToken);
        if (tokenResponse.StatusCode != HttpStatusCode.NoContent)
        {
            return tokenResponse;
        }

        AntiforgeryToken token;
        using (tokenResponse)
        {
            token = AntiforgeryToken.From(tokenResponse);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(token.HeaderName, token.Value);
        return await SendAsync(request, cancellationToken);
    }

    /// <summary>POST com o token informado (sem buscar outro): o mesmo token vale enquanto a identidade não muda.</summary>
    public Task<HttpResponseMessage> PostWithTokenAsync(string path, object? body, AntiforgeryToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(token.HeaderName, token.Value);
        return SendAsync(request, cancellationToken);
    }

    public Task<HttpResponseMessage> RegisterAsync(TestUser user, CancellationToken cancellationToken) =>
        PostAsync(IdentityApi.RegisterPath, IdentityApi.RegisterBody(user), cancellationToken);

    public Task<HttpResponseMessage> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        PostAsync(IdentityApi.LoginPath, new { email, password }, cancellationToken);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using (request)
        {
            foreach (var (name, value) in ExtraHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            return await _client.SendAsync(request, timeout.Token);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Endereços de teste (RFC 5737 para clientes; faixa privada para a rede do Compose).</summary>
internal static class TestAddresses
{
    /// <summary>Rede do proxy configurada como confiável nos testes.</summary>
    public const string TrustedProxyNetwork = "172.30.0.0/16";

    /// <summary>O Caddy, dentro da rede confiável.</summary>
    public static readonly IPAddress TrustedProxy = IPAddress.Parse("172.30.0.2");

    /// <summary>Endereço fora da rede confiável que fala direto com a Api.</summary>
    public static readonly IPAddress UntrustedPeer = IPAddress.Parse("203.0.113.66");

    /// <summary>Um cliente público distinto por <paramref name="index"/> (198.51.100.0/24).</summary>
    public static IPAddress Client(int index) => IPAddress.Parse($"198.51.100.{index + 1}");
}
