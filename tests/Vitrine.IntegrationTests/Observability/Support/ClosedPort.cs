using System.Net;
using System.Net.Sockets;

namespace Vitrine.IntegrationTests.Observability.Support;

internal static class ClosedPort
{
    /// <summary>URL local numa porta que acabou de ser liberada e em que ninguém escuta (destino OTLP inacessível).</summary>
    public static Uri NewLocalUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }
}
