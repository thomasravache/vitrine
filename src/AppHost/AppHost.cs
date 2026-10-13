var builder = DistributedApplication.CreateBuilder(args);

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
