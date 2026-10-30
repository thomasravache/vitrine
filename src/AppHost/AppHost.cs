var builder = DistributedApplication.CreateBuilder(args);

// Publicação: `aspire publish` gera o docker-compose.yaml + .env do Compose (ADR-0004, SPEC-0004).
builder.AddDockerComposeEnvironment("compose");

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("vitrine");

var api = builder.AddProject<Projects.Vitrine_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/live");

var web = builder.AddViteApp("web", "../../web")
    .WithReference(api)
    .WaitFor(api);

// Simulador do Mercado Livre (SPEC-0024, ADR-0016): só no `aspire run` e no E2E, nunca no `aspire publish`.
if (builder.ExecutionContext.IsRunMode)
{
    // Credenciais SINTÉTICAS e públicas, só do simulador (nunca as do app real do DevCenter).
    const string SimulatorClientId = "1000000000000024";
    const string SimulatorClientSecret = "simulador-dev-sintetico-nao-secreto";

    var simulator = builder.AddProject<Projects.Vitrine_MercadoLivreSimulator>("ml-simulator")
        .WithHttpHealthCheck("/health");
    var simulatorUrl = simulator.GetEndpoint("http");
    var redirectUri = ReferenceExpression.Create($"{web.GetEndpoint("http")}/contas/mercado-livre/retorno");

    api.WithEnvironment("MercadoLivre__AuthorizationBaseUrl", simulatorUrl)
        .WithEnvironment("MercadoLivre__ApiBaseUrl", simulatorUrl)
        .WithEnvironment("MercadoLivre__ClientId", SimulatorClientId)
        .WithEnvironment("MercadoLivre__ClientSecret", SimulatorClientSecret)
        .WithEnvironment("MercadoLivre__RedirectUri", redirectUri);

    simulator.WithEnvironment("Simulator__ClientId", SimulatorClientId)
        .WithEnvironment("Simulator__ClientSecret", SimulatorClientSecret)
        .WithEnvironment("Simulator__AllowedRedirectUris__0", redirectUri)
        .WithEnvironment("Simulator__NotificationUrl", ReferenceExpression.Create($"{api.GetEndpoint("http")}/api/v1/mercadolivre/notifications"));
}

builder.Build().Run();
