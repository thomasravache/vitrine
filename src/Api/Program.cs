using JasperFx;
using JasperFx.Resources;
using Vitrine.Identity.Infrastructure;
using Vitrine.Identity.Presentation;
using Wolverine;
using Wolverine.Postgresql;

// `--migrate`: modo de migração do pipeline (ADR-0011, SPEC-0005) — aplica o schema e sai; nunca sobe o Kestrel.
const string MigrateArgument = "--migrate";
var migrateOnly = args.Contains(MigrateArgument, StringComparer.Ordinal);

var builder = WebApplication.CreateBuilder(args.Where(arg => arg != MigrateArgument).ToArray());

builder.AddServiceDefaults();

// Módulos (ADR-0001).
builder.Services.AddIdentityModule(builder.Configuration);

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

if (migrateOnly)
{
    return await MigrateAsync(app);
}

app.MapDefaultEndpoints();
app.MapIdentityEndpoints();

await app.RunAsync();
return 0;

// Aplica, em ordem, as migrações dos DbContexts dos módulos e o schema do Wolverine, sem iniciar o host
// (nem Kestrel nem serviços hospedados). 0 = ok; 1 = falha (o deploy aborta sem trocar a versão).
static async Task<int> MigrateAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vitrine.Migrate");
    try
    {
        // 1. DbContexts dos módulos: nenhum registrado ainda. Cada módulo que criar o seu registra as migrações
        //    como recurso do Wolverine/JasperFx (IStatefulResource), aplicado aqui junto com o armazenamento.
        // 2. Armazenamento de mensagens do Wolverine (inbox/outbox, filas duráveis).
        MigrationLog.Starting(logger);
        await app.SetupResources();
        MigrationLog.Completed(logger);
        return 0;
    }
#pragma warning disable CA1031 // Qualquer falha de migração vira código de saída != 0 para o pipeline.
    catch (Exception exception)
#pragma warning restore CA1031
    {
        MigrationLog.Failed(logger, exception);
        return 1;
    }
}

/// <summary>Mensagens de log do modo <c>--migrate</c> (sem segredos: nunca registra a connection string).</summary>
internal static partial class MigrationLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Migração: aplicando o schema dos recursos registrados (Wolverine).")]
    public static partial void Starting(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Migração concluída.")]
    public static partial void Completed(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Critical, Message = "Migração falhou.")]
    public static partial void Failed(ILogger logger, Exception exception);
}

/// <summary>Exposto para <c>WebApplicationFactory&lt;Program&gt;</c> nos testes de integração (SPEC-0003).</summary>
public partial class Program;
