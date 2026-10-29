using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// Simula o IP de origem da conexão TCP no host em memória: o TestServer não tem conexão de rede, então o teste
/// informa o IP no cabeçalho <see cref="HeaderName"/> e este filtro de inicialização — o primeiro middleware do
/// pipeline, antes de qualquer middleware do <c>Program</c> (inclusive <c>UseForwardedHeaders</c>) — o grava em
/// <see cref="ConnectionInfo.RemoteIpAddress"/> e remove o cabeçalho. Para a Api é como se a conexão viesse desse IP:
/// o proxy (Caddy) quando o IP é da rede confiável, ou um cliente direto quando não é.
/// </summary>
internal sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.Remove(HeaderName, out var value) && IPAddress.TryParse(value.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await nextMiddleware(context);
        });
        next(app);
    };
}

/// <summary>Faz cada requisição de um cliente chegar à Api como vinda de <paramref name="remoteIp"/>.</summary>
internal sealed class TestRemoteIpHandler(IPAddress remoteIp) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(TestRemoteIpStartupFilter.HeaderName);
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp.ToString());
        return base.SendAsync(request, cancellationToken);
    }
}
