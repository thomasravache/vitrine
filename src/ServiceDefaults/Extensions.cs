using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vitrine.ServiceDefaults.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Padrões Aspire comuns a todo serviço do Vitrine: OpenTelemetry, health checks,
/// service discovery e resiliência HTTP (ADR-0001). Referenciado por cada host em <c>src/</c>.
/// </summary>
public static class Extensions
{
    /// <summary>Caminho de liveness: só checagens marcadas com <see cref="LiveTag"/>; sem dependências externas.</summary>
    public const string LivenessEndpointPath = "/health/live";

    /// <summary>Caminho de readiness: só checagens marcadas com <see cref="ReadyTag"/> (ex.: banco de dados).</summary>
    public const string ReadinessEndpointPath = "/health/ready";

    /// <summary>Marca das checagens de dependências exigidas para atender (readiness).</summary>
    public const string ReadyTag = "ready";

    private const string HealthEndpointPrefix = "/health";
    private const string LiveTag = "live";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // Mascaramento (ADR-0006) antes de qualquer exportador: os processadores rodam na ordem de registro, e
            // este é registrado aqui, antes do OTLP (abaixo) e de exportadores acrescentados pelo host.
            logging.AddProcessor(new SensitiveDataMaskingProcessor());
        });

        // Fora do desenvolvimento, console em JSON (uma linha por registro, com trace_id) e mascarado como o OTLP.
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.AddConsoleFormatter<MaskedJsonConsoleFormatter, ConsoleFormatterOptions>(options =>
                options.IncludeScopes = true);
            builder.Logging.AddConsole(options => options.FormatterName = MaskedJsonConsoleFormatter.FormatterName);
        }

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                        // Requisições de health check não geram traces.
                        options.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpointPrefix))
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHealthChecks()
            // Liveness: o processo responde; não depende de banco nem de terceiros.
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    /// <summary>
    /// Mapeia <c>GET /health/live</c> e <c>GET /health/ready</c> em todos os ambientes. Os endpoints só expõem o
    /// estado agregado (Healthy/Unhealthy; 503 quando Unhealthy), sem nomes de checagem, hosts nem exceções:
    /// liveness em texto (<c>Healthy</c>) e readiness em JSON (<c>{"status":"Healthy"}</c>).
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(LivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        });

        app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteStatusOnlyAsync,
        });

        return app;
    }

    // Só o estado agregado: nada de entradas por checagem, descrições, durações ou exceções.
    private static Task WriteStatusOnlyAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync($$"""{"status":"{{report.Status}}"}""");
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
