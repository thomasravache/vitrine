var builder = DistributedApplication.CreateBuilder(args);

// Publicação: `aspire publish` gera o docker-compose.yaml + .env do Compose (ADR-0004, SPEC-0004).
builder.AddDockerComposeEnvironment("compose");

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("vitrine");

var api = builder.AddProject<Projects.Vitrine_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health/live");

builder.AddViteApp("web", "../../web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
