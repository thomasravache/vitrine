using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.MercadoLivreSimulator;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

/// <summary>
/// Simulador do Mercado Livre (SPEC-0024) em memória, com a configuração <c>Simulator:*</c> do contrato (credenciais
/// sintéticas) e o relógio <see cref="Clock"/> no lugar do <see cref="TimeProvider"/> (ex.: vencer o code de 10 min).
/// Um host por teste: o estado do simulador é só dele.
/// </summary>
internal sealed class SimulatorHost(string? notificationUrl = null) : WebApplicationFactory<SimulatorEntryPoint>
{
    /// <summary>client_id sintético (numérico, como o <c>application_id</c> do DevCenter).</summary>
    public const string ClientId = "1234567890123456";

    /// <summary>client_secret sintético e público (só para testes).</summary>
    public const string ClientSecret = "simulador-segredo-sintetico-publico";

    /// <summary>Redirect URI aceito (<c>Simulator:AllowedRedirectUris</c>).</summary>
    public const string RedirectUri = "https://vitrine.test/contas/mercado-livre/retorno";

    /// <summary>Segundo redirect URI aceito (para o caso "redirect_uri diferente" na troca).</summary>
    public const string OtherRedirectUri = "https://outro.vitrine.test/contas/mercado-livre/retorno";

    /// <summary>Vendedor da massa inicial (site MLB, 25 anúncios: 15 ativos, 6 pausados, 4 encerrados).</summary>
    public const long SellerTeste = 3_000_000_001;

    /// <summary>Vendedor da massa inicial no site MLA (site não suportado pelo Vitrine).</summary>
    public const long SellerAr = 3_000_000_002;

    /// <summary>Relógio do simulador (começa no instante real; avança só quando o teste pede).</summary>
    public AdjustableClock Clock { get; } = new();

    /// <summary>Cliente sem seguir redirecionamentos (o 302 da autorização é verificado pelo teste).</summary>
    public SimulatorClient NewClient() =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.UseSetting("Simulator:ClientId", ClientId);
        builder.UseSetting("Simulator:ClientSecret", ClientSecret);
        builder.UseSetting("Simulator:AllowedRedirectUris:0", RedirectUri);
        builder.UseSetting("Simulator:AllowedRedirectUris:1", OtherRedirectUri);
        if (notificationUrl is not null)
        {
            builder.UseSetting("Simulator:NotificationUrl", notificationUrl);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}
