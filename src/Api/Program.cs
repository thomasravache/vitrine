using System.Reflection;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Vitrine.Identity.Infrastructure;
using Vitrine.Identity.Presentation;
using Wolverine;
using Wolverine.Postgresql;

// `--migrate`: modo de migração do pipeline (ADR-0011, SPEC-0005) — aplica o schema e sai; nunca sobe o Kestrel.
const string MigrateArgument = "--migrate";
var migrateOnly = args.Contains(MigrateArgument, StringComparer.Ordinal);

// Geração do documento OpenAPI no build (GetDocument.Insider sobe o host): sem banco nem mensageria.
var generatingOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

var builder = WebApplication.CreateBuilder(args.Where(arg => arg != MigrateArgument).ToArray());

builder.AddServiceDefaults();

// Borda HTTP: erros em application/problem+json, documento OpenAPI (fonte do cliente da SPA — ADR-0013).
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Acesso (ADR-0007): autorização negada por padrão; endpoints públicos usam AllowAnonymous. Os health checks
// (ServiceDefaults) são públicos por natureza: sondas do orquestrador não têm sessão.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAssertion(context => context.User.Identity?.IsAuthenticated == true
            || (context.Resource is HttpContext http && http.Request.Path.StartsWithSegments(HealthPathPrefix)))
        .Build());

// CSRF (ADR-0007): a SPA busca o token em GET /api/v1/auth/antiforgery e o devolve neste cabeçalho nos POST.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "vitrine.antiforgery";
    // SameAsRequest: com Always o antiforgery recusa (500) requisições que chegam em HTTP ao Kestrel — o caso do
    // proxy TLS (Caddy) sem cabeçalhos encaminhados e do Vite em desenvolvimento. Em HTTPS o cookie sai Secure.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Módulos (ADR-0001).
builder.Services.AddIdentityModule(builder.Configuration);

// Mensageria interna e jobs com filas duráveis e outbox/inbox no PostgreSQL (ADR-0014).
if (!generatingOpenApi)
{
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
}

// Desenvolvimento (aspire run / E2E pelo AppHost): aplica as migrações dos módulos na subida, como o Wolverine
// acima. Fora dele, só o modo --migrate do pipeline altera o schema (ADR-0011).
if (builder.Environment.IsDevelopment() && !migrateOnly && !generatingOpenApi)
{
    builder.Services.AddResourceSetupOnStartup();
}

var app = builder.Build();

if (migrateOnly)
{
    return await MigrateAsync(app);
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// SPA estática (web/dist em wwwroot, só na imagem publicada — ADR-0013), pública e antes da autenticação.
var hasSpa = app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists;
if (hasSpa)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapIdentityEndpoints();

if (hasSpa)
{
    // Rotas da SPA (/cadastro, /painel, ...) caem no index.html; /api desconhecida continua 404.
    app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
    app.MapFallbackToFile("index.html").AllowAnonymous();
}

await app.RunAsync();
return 0;

// Aplica, em ordem, as migrações dos DbContexts dos módulos e o schema do Wolverine, sem iniciar o host
// (nem Kestrel nem serviços hospedados). 0 = ok; 1 = falha (o deploy aborta sem trocar a versão).
static async Task<int> MigrateAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vitrine.Migrate");
    try
    {
        // Recursos com estado registrados: migrações EF Core de cada módulo (registradas pelo próprio módulo,
        // ex.: AddIdentityModule) e o armazenamento de mensagens do Wolverine (inbox/outbox, filas duráveis).
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
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Migração: aplicando o schema dos recursos registrados (módulos e Wolverine).")]
    public static partial void Starting(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Migração concluída.")]
    public static partial void Completed(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Critical, Message = "Migração falhou.")]
    public static partial void Failed(ILogger logger, Exception exception);
}

/// <summary>Exposto para <c>WebApplicationFactory&lt;Program&gt;</c> nos testes de integração (SPEC-0003).</summary>
public partial class Program
{
    private const string HealthPathPrefix = "/health";
}
