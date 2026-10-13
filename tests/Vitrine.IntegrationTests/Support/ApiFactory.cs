using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;

namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// Host da Api em memória apontando para um banco isolado (<c>ConnectionStrings:vitrine</c>).
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> devolve um HttpClient que mantém cookies;
/// serviços HTTP externos são trocados pelo WireMock em <see cref="ExternalServices"/>.
/// Configure (WithServices/RedirectToWireMock) antes do primeiro uso de Services ou CreateClient.
/// </summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Connection string do banco usado pela Api.</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>Servidor HTTP falso que atende os serviços externos.</summary>
    public WireMockServer ExternalServices =>
        throw new NotImplementedException("SPEC-0003: ApiFactory (WireMock) ainda não implementada.");

    /// <summary>Acrescenta registros de serviço ao host de teste.</summary>
    public ApiFactory WithServices(Action<IServiceCollection> configure) =>
        throw new NotImplementedException("SPEC-0003: ApiFactory.WithServices ainda não implementado.");

    /// <summary>Faz o HttpClient nomeado <paramref name="httpClientName"/> ser atendido por <see cref="ExternalServices"/>.</summary>
    public ApiFactory RedirectToWireMock(string httpClientName) =>
        throw new NotImplementedException("SPEC-0003: ApiFactory.RedirectToWireMock ainda não implementado.");
}
