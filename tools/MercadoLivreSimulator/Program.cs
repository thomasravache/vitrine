using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>
/// Simulador do Mercado Livre (SPEC-0024, ADR-0016): só para desenvolvimento, E2E e ambiente local prod-like. Estado em
/// memória; configuração <c>Simulator:*</c> (sem appsettings.json). <c>Program</c> com namespace (sem instruções de
/// nível superior) para não colidir com o <c>Program</c> público da Api nos projetos de teste que referenciam os dois.
/// </summary>
internal static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddOptions<SimulatorOptions>()
            .Bind(builder.Configuration.GetSection(SimulatorOptions.SectionName))
            .Validate(options => !options.Problems().Any(), "Configuração Simulator:* inválida (ClientId numérico, ClientSecret, AllowedRedirectUris, NotificationUrl, AccessTokenLifetime).")
            .ValidateOnStart();
        builder.Services.AddSingleton<SimulatorState>();
        builder.Services.AddSingleton<ItemSearch>();
        builder.Services.AddSingleton(services => new TokenIssuer(
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IOptions<SimulatorOptions>>().Value.ParsedAccessTokenLifetime()!.Value));
        builder.Services.AddHttpClient(ControlEndpoints.NotificationClient, client => client.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        app.MapHealthChecks("/health");
        app.MapMercadoLivre();
        app.MapControl();
        app.Run();
    }
}
