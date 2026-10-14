using JasperFx;
using Wolverine;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Mensageria interna e jobs com filas duráveis e outbox/inbox no PostgreSQL (ADR-0014).
builder.Host.UseWolverine(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("vitrine")
        ?? throw new InvalidOperationException("ConnectionStrings:vitrine não configurada (o AppHost a fornece).");

    options.PersistMessagesWithPostgresql(connectionString);

    // Schema só no passo dedicado de migração fora do desenvolvimento (ADR-0011); testes ligam por configuração.
    var buildStorageOnStartup = builder.Environment.IsDevelopment()
        || builder.Configuration.GetValue<bool>("Wolverine:AutoBuildMessageStorageOnStartup");
    options.AutoBuildMessageStorageOnStartup = buildStorageOnStartup ? AutoCreate.CreateOrUpdate : AutoCreate.None;
    options.UseRuntimeCompilation();
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Exposto para <c>WebApplicationFactory&lt;Program&gt;</c> nos testes de integração (SPEC-0003).</summary>
public partial class Program;
