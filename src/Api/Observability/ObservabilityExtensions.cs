using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Vitrine.Identity.Infrastructure;
using Vitrine.ServiceDefaults.Logging;

namespace Vitrine.Api.Observability;

/// <summary>
/// Observabilidade específica da Api (SPEC-0007, ADR-0009), além dos padrões de serviço (<c>AddServiceDefaults</c>):
/// readiness do PostgreSQL, spans de banco, métricas dos módulos e Sentry opcional.
/// </summary>
internal static class ObservabilityExtensions
{
    /// <summary>Chave do DSN do Sentry (<c>Sentry__Dsn</c> no ambiente). Sem ela, o Sentry não é ligado.</summary>
    public const string SentryDsnKey = "Sentry:Dsn";

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(5);

    public static WebApplicationBuilder AddApiObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // /health/ready inclui o banco (ADR-0009); /health/live continua só com o processo.
        builder.Services.AddHealthChecks()
            .AddCheck<PostgresReadinessCheck>("postgres", HealthStatus.Unhealthy, [Extensions.ReadyTag], ReadinessTimeout);

        // Spans do Npgsql (filhos do span HTTP) e métricas da jornada de acesso (vitrine.auth.*).
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddNpgsql())
            .WithMetrics(metrics => metrics.AddMeter(IdentityModule.MeterName));

        // Sentry só com DSN configurado (secret do Environment); sem ele, nada é inicializado (exportação opcional).
        if (!string.IsNullOrWhiteSpace(builder.Configuration[SentryDsnKey]))
        {
            builder.WebHost.UseSentry(ConfigureSentry);
        }

        return builder;
    }

    // Release = SHA da imagem (Sentry__Release, gravado pelo deploy) e ambiente; PII desligada e eventos mascarados.
    private static void ConfigureSentry(WebHostBuilderContext context, SentryAspNetCoreOptions options)
    {
        options.SendDefaultPii = false;
        options.Environment ??= context.HostingEnvironment.EnvironmentName.ToLowerInvariant();
        options.MaxRequestBodySize = RequestSize.None;
        options.SetBeforeSend(MaskEvent);
        options.SetBeforeBreadcrumb(MaskBreadcrumb);
    }

    private static SentryEvent MaskEvent(SentryEvent sentryEvent)
    {
        if (sentryEvent.Message is { } message)
        {
            message.Formatted = message.Formatted is null ? null : SensitiveDataMasker.MaskText(message.Formatted);
            message.Message = message.Message is null ? null : SensitiveDataMasker.MaskText(message.Message);
        }

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = exception.Value is null ? null : SensitiveDataMasker.MaskText(exception.Value);
        }

        var request = sentryEvent.Request;
        request.Cookies = null;
        request.Data = null;
        // Query e URL: valor de parâmetro de nome sensível (access_token, email...) vira ***, e-mails também.
        request.QueryString = request.QueryString is null ? null : SensitiveDataMasker.MaskText(request.QueryString);
        request.Url = request.Url is null ? null : SensitiveDataMasker.MaskText(request.Url);
        foreach (var (name, value) in request.Headers.ToList())
        {
            // Cabeçalho de nome sensível (Authorization, Cookie...) inteiro; os demais (ex.: Referer com query) por texto.
            request.Headers[name] = SensitiveDataMasker.IsSensitiveName(name) ? SensitiveDataMasker.Mask : SensitiveDataMasker.MaskText(value);
        }

        foreach (var (key, value) in sentryEvent.Extra.ToList())
        {
            sentryEvent.SetExtra(key, SensitiveDataMasker.IsSensitiveName(key)
                ? SensitiveDataMasker.Mask
                : value is string text ? SensitiveDataMasker.MaskText(text) : value);
        }

        return sentryEvent;
    }

    // Breadcrumbs vêm dos logs (ILogger) e das requisições: mesmas regras do pipeline do OpenTelemetry. O carimbo de
    // tempo é refeito no momento da cópia (diferença desprezível; o construtor com carimbo não é público).
    private static Breadcrumb MaskBreadcrumb(Breadcrumb breadcrumb) => new(
        breadcrumb.Message is null ? string.Empty : SensitiveDataMasker.MaskText(breadcrumb.Message),
        breadcrumb.Type ?? "default",
        breadcrumb.Data?.ToDictionary(
            entry => entry.Key,
            entry => SensitiveDataMasker.IsSensitiveName(entry.Key) ? SensitiveDataMasker.Mask : SensitiveDataMasker.MaskText(entry.Value),
            StringComparer.Ordinal),
        breadcrumb.Category,
        breadcrumb.Level);
}
