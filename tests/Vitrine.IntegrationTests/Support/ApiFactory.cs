using JasperFx.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;

namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// Host da Api em memória apontando para um banco isolado (<c>ConnectionStrings:vitrine</c>).
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> devolve um HttpClient que mantém cookies;
/// serviços HTTP externos são trocados pelo WireMock em <see cref="ExternalServices"/>.
/// Na subida, o host aplica todo recurso com estado registrado (schema do Wolverine e migrações dos módulos,
/// registradas como <c>IStatefulResource</c> — ADR-0011), já que o banco do teste nasce vazio.
/// Configure (WithSetting/WithServices/RedirectToWireMock) antes do primeiro uso de Services ou CreateClient.
/// </summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    private readonly List<KeyValuePair<string, string?>> _settings = [];
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private readonly List<string> _redirectedClients = [];
    private readonly Lazy<WireMockServer> _externalServices = new(() => WireMockServer.Start());

    /// <summary>Connection string do banco usado pela Api.</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>Servidor HTTP falso que atende os serviços externos.</summary>
    public WireMockServer ExternalServices => _externalServices.Value;

    /// <summary>Define uma chave de configuração do host (ex.: <c>Features:SignupOpen</c>).</summary>
    public ApiFactory WithSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _settings.Add(new(key, value));
        return this;
    }

    /// <summary>Acrescenta registros de serviço ao host de teste.</summary>
    public ApiFactory WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigurations.Add(configure);
        return this;
    }

    /// <summary>Faz o HttpClient nomeado <paramref name="httpClientName"/> ser atendido por <see cref="ExternalServices"/>.</summary>
    public ApiFactory RedirectToWireMock(string httpClientName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        _redirectedClients.Add(httpClientName);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:vitrine", ConnectionString);
        // O banco do teste nasce vazio: o host cria o schema do Wolverine em qualquer ambiente de teste.
        builder.UseSetting("Wolverine:AutoBuildMessageStorageOnStartup", "true");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Banco vazio: aplica os recursos com estado (Wolverine + migrações dos módulos) antes de atender.
            services.AddResourceSetupOnStartup(StartupAction.SetupOnly);

            foreach (var configure in _serviceConfigurations)
            {
                configure(services);
            }

            foreach (var name in _redirectedClients)
            {
                var target = new Uri(ExternalServices.Url!);
                services.AddHttpClient(name).AddHttpMessageHandler(() => new RedirectHandler(target));
            }
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_externalServices.IsValueCreated)
        {
            _externalServices.Value.Dispose();
        }
    }

    /// <summary>Reescreve esquema, host e porta de toda requisição para o WireMock: nada sai para a rede.</summary>
    private sealed class RedirectHandler(Uri target) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { IsAbsoluteUri: true } original)
            {
                request.RequestUri = new UriBuilder(original)
                {
                    Scheme = target.Scheme,
                    Host = target.Host,
                    Port = target.Port,
                }.Uri;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
