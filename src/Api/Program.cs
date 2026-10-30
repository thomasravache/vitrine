using System.Reflection;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Vitrine.Api;
using Vitrine.Api.Observability;
using Vitrine.Api.Security;
using Vitrine.Identity.Infrastructure;
using Vitrine.Identity.Presentation;
using Vitrine.MercadoLivre.Infrastructure;
using Vitrine.MercadoLivre.Presentation;
using Wolverine;
using Wolverine.Postgresql;

// `--migrate`: modo de migração do pipeline (ADR-0011, SPEC-0005) — aplica o schema e sai; nunca sobe o Kestrel.
const string MigrateArgument = "--migrate";
var migrateOnly = args.Contains(MigrateArgument, StringComparer.Ordinal);

// Geração do documento OpenAPI no build (GetDocument.Insider sobe o host): sem banco, mensageria nem chave mestra
// (SPEC-0011), em qualquer ambiente — `npm run api:generate` e o passo de drift do CI rodam sem PostgreSQL.
var generatingOpenApi = OpenApiGeneration.IsGeneratingDocument(Assembly.GetEntryAssembly());

var builder = WebApplication.CreateBuilder(args.Where(arg => arg != MigrateArgument).ToArray());

builder.AddServiceDefaults();

// Observabilidade da Api (SPEC-0007): readiness do banco, spans do Npgsql, métricas vitrine.auth.* e Sentry opcional.
builder.AddApiObservability();

// Borda HTTP: erros em application/problem+json, documento OpenAPI (fonte do cliente da SPA — ADR-0013).
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    // Todo erro tem "code" (contrato da API); os erros dos módulos já trazem o seu, os do framework ganham um genérico.
    context.ProblemDetails.Extensions.TryAdd("code", ProblemCodes.For(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode)));
builder.Services.AddOpenApi();

// Acesso (ADR-0007, SPEC-0008): autorização negada por padrão, sem exceção por caminho. Só a lista AnonymousEndpoints
// do contrato é pública, cada endpoint com AllowAnonymous (auth, health checks do ServiceDefaults e fallbacks da SPA).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// CSRF (ADR-0007): a SPA busca o token em GET /api/v1/auth/antiforgery e o devolve neste cabeçalho nos POST.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "vitrine.antiforgery";
    // Always: atrás do Caddy a Api enxerga HTTPS pelos forwarded headers (SPEC-0008); requisição em HTTP puro nos
    // endpoints de auth recebe 400. Só em Development (Vite e Api em http://localhost) o cookie segue a requisição.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Segurança de base (SPEC-0008): forwarded headers, cabeçalhos, rate limiting de auth, Data Protection e cifra de campos.
// Na geração do OpenAPI (SPEC-0011): Data Protection efêmero, sem chave mestra nem banco, e contêiner sem ValidateOnBuild.
OpenApiGeneration.Configure(builder, generatingOpenApi);

// Módulos (ADR-0001).
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddMercadoLivreModule(builder.Configuration);

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

// Antes de tudo: IP/esquema reais vindos do proxy confiável e cabeçalhos de segurança em toda resposta.
app.UseEdgeSecurity();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Requisição malformada (JSON inválido, corpo ausente...) é 400, não 500, em qualquer ambiente.
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

// Limites de /api/v1/auth/* (429 + Retry-After) com o IP real do cliente.
app.UseAuthRateLimiting();

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
app.MapMercadoLivreEndpoints();

// /api e /health desconhecidas: 404 (problem+json) com ou sem sessão — nem 401 da política de fallback, nem a SPA.
app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
app.MapFallback("/health/{**path}", () => Results.NotFound()).AllowAnonymous();

if (hasSpa)
{
    // Rotas da SPA (/cadastro, /painel, ...) caem no index.html.
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

/// <summary>Código genérico (<c>code</c>) dos problemas gerados pelo framework, por status HTTP.</summary>
internal static class ProblemCodes
{
    public static string For(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "BAD_REQUEST",
        StatusCodes.Status401Unauthorized => "UNAUTHORIZED",
        StatusCodes.Status403Forbidden => "FORBIDDEN",
        StatusCodes.Status404NotFound => "NOT_FOUND",
        StatusCodes.Status405MethodNotAllowed => "METHOD_NOT_ALLOWED",
        StatusCodes.Status415UnsupportedMediaType => "UNSUPPORTED_MEDIA_TYPE",
        StatusCodes.Status429TooManyRequests => "TOO_MANY_REQUESTS",
        >= 500 => "INTERNAL_ERROR",
        _ => $"HTTP_{status}",
    };
}

/// <summary>Exposto para <c>WebApplicationFactory&lt;Program&gt;</c> nos testes de integração (SPEC-0003).</summary>
public partial class Program;
