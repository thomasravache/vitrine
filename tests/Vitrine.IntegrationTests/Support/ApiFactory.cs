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
/// Segurança (SPEC-0008): cada host recebe uma chave mestra do Data Protection gerada por execução
/// (<see cref="MasterKey"/>) e roda com os limites de requisição de auth do contrato (nada é relaxado). O IP de origem
/// é simulado pelo <see cref="TestRemoteIpStartupFilter"/>: o cliente que não informa IP (<see cref="TestRemoteIpHandler"/>)
/// chega de um IP novo a cada requisição, então o limite por IP só pesa nos testes que fixam o IP de propósito.
/// Configure (WithSetting/WithServices/RedirectToWireMock) antes do primeiro uso de Services ou CreateClient.
/// </summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    private readonly List<KeyValuePair<string, string?>> _settings = [];
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private readonly List<string> _redirectedClients = [];
    private readonly Lazy<WireMockServer> _externalServices = new(() => WireMockServer.Start());
    private string? _environment;
    private string? _webRoot;

    /// <summary>Connection string do banco usado pela Api.</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>Chave mestra do Data Protection deste host (gerada por execução; troque com <see cref="WithMasterKey"/>).</summary>
    public string MasterKey { get; private set; } = SecuritySettings.NewMasterKey();

    /// <summary>Servidor HTTP falso que atende os serviços externos.</summary>
    public WireMockServer ExternalServices => _externalServices.Value;

    /// <summary>Define uma chave de configuração do host (ex.: <c>Features:SignupOpen</c>).</summary>
    public ApiFactory WithSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _settings.Add(new(key, value));
        return this;
    }

    /// <summary>Usa a chave mestra informada (ex.: a mesma de um host anterior, simulando o reinício do container).</summary>
    public ApiFactory WithMasterKey(string masterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        MasterKey = masterKey;
        return this;
    }

    /// <summary>Sobe o host no ambiente informado (ex.: <c>Production</c>); sem isso, o padrão do WebApplicationFactory.</summary>
    public ApiFactory WithEnvironment(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        _environment = environment;
        return this;
    }

    /// <summary>Usa <paramref name="webRoot"/> como web root (ex.: <see cref="TestWebRoot"/> com um index.html de teste).</summary>
    public ApiFactory WithWebRoot(string webRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);
        _webRoot = webRoot;
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
        builder.UseSetting(SecuritySettings.MasterKey, MasterKey);
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        if (_webRoot is not null)
        {
            builder.UseWebRoot(_webRoot);
        }

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Banco vazio: aplica os recursos com estado (Wolverine + migrações dos módulos) antes de atender.
            services.AddResourceSetupOnStartup(StartupAction.SetupOnly);

            // IP de origem simulado por requisição (cabeçalho de teste), aplicado antes de todo o pipeline da Api.
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();

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
