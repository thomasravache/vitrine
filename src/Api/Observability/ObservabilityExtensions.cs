using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Vitrine.Identity.Infrastructure;
using Vitrine.MercadoLivre.Infrastructure;
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

        // Spans do Npgsql (filhos do span HTTP) e métricas dos módulos (vitrine.auth.*, vitrine.mercadolivre.*).
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddNpgsql())
            .WithMetrics(metrics => metrics.AddMeter(IdentityModule.MeterName, MercadoLivreModule.MeterName));

        // Sentry só com DSN configurado (secret do Environment); sem ele, nada é inicializado (exportação opcional).
        if (!string.IsNullOrWhiteSpace(builder.Configuration[SentryDsnKey]))
        {
            builder.WebHost.UseSentry(ConfigureSentry);
            MaskSentryLoggerProviders(builder.Services);
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

    /// <summary>
    /// Troca cada <see cref="ILoggerProvider"/> do Sentry (eventos e breadcrumbs vindos do <c>ILogger</c>) por um
    /// <see cref="MaskingLoggerProvider"/> em volta dele: propriedades, tags e mensagem chegam ao Sentry mascaradas.
    /// </summary>
    private static void MaskSentryLoggerProviders(IServiceCollection services)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(ILoggerProvider) || descriptor.IsKeyedService)
            {
                continue;
            }

            var implementation = descriptor.ImplementationType
                ?? descriptor.ImplementationInstance?.GetType()
                ?? descriptor.ImplementationFactory?.Method.ReturnType;
            if (implementation?.Namespace?.StartsWith("Sentry", StringComparison.Ordinal) != true)
            {
                continue;
            }

            services[index] = ServiceDescriptor.Describe(
                typeof(ILoggerProvider),
                provider => new MaskingLoggerProvider(Create(descriptor, provider)),
                descriptor.Lifetime);
        }

        // Regras de filtro registradas pelo tipo do provedor do Sentry passam a valer também para o decorador.
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            var decorator = typeof(MaskingLoggerProvider).FullName;
            foreach (var rule in options.Rules.Where(rule => rule.ProviderName?.StartsWith("Sentry.", StringComparison.Ordinal) == true).ToList())
            {
                options.Rules.Add(new LoggerFilterRule(decorator, rule.CategoryName, rule.LogLevel, rule.Filter));
            }
        });
    }

    private static ILoggerProvider Create(ServiceDescriptor descriptor, IServiceProvider provider) =>
        (ILoggerProvider)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));

    private static SentryEvent MaskEvent(SentryEvent sentryEvent)
    {
        // Tags vêm das propriedades do log (já mascaradas pelo MaskingLoggerProvider); aqui, defesa para tags
        // acrescentadas por outros caminhos. Valores de tags sensíveis e das propriedades sensíveis do registro de log
        // (o mesmo segredo repetido no texto da exceção) também saem da mensagem e das exceções.
        var sensitiveValues = new List<string>(MaskingLoggerProvider.CurrentSensitiveValues);
        foreach (var (key, value) in sentryEvent.Tags.ToList())
        {
            if (SensitiveDataMasker.IsSensitiveName(key))
            {
                if (!string.IsNullOrEmpty(value) && value != SensitiveDataMasker.Mask)
                {
                    sensitiveValues.Add(value);
                }

                sentryEvent.SetTag(key, SensitiveDataMasker.Mask);
            }
            else
            {
                sentryEvent.SetTag(key, SensitiveDataMasker.MaskText(value));
            }
        }

        // Valores das chaves sensíveis de Exception.Data (Mechanism.Data), contexts e extras também saem da mensagem,
        // das exceções e dos breadcrumbs (SPEC-0018): a lista fica completa antes de mascarar qualquer texto.
        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            if (exception.Mechanism is { } mechanism)
            {
                SentryDataMasker.CollectSensitiveValues(null, mechanism.Data, sensitiveValues);
            }
        }

        foreach (var (key, value) in CustomContexts(sentryEvent.Contexts))
        {
            SentryDataMasker.CollectSensitiveValues(key, value, sensitiveValues);
        }

        foreach (var (key, value) in sentryEvent.Extra)
        {
            SentryDataMasker.CollectSensitiveValues(key, value, sensitiveValues);
        }

        if (sentryEvent.Message is { } message)
        {
            message.Formatted = message.Formatted is null ? null : SensitiveDataMasker.MaskText(message.Formatted, sensitiveValues);
            message.Message = message.Message is null ? null : SensitiveDataMasker.MaskText(message.Message, sensitiveValues);
        }

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = exception.Value is null ? null : SensitiveDataMasker.MaskText(exception.Value, sensitiveValues);
            if (exception.Mechanism is { } mechanism)
            {
                MaskMechanismData(mechanism.Data, sensitiveValues);
            }
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

        // Extras e contexts próprios (ex.: Exception.Data com prefixo sentry:) com valores de qualquer tipo.
        foreach (var (key, value) in sentryEvent.Extra.ToList())
        {
            sentryEvent.SetExtra(key, SentryDataMasker.MaskValue(key, value, sensitiveValues));
        }

        foreach (var (key, value) in CustomContexts(sentryEvent.Contexts))
        {
            sentryEvent.Contexts[key] = SentryDataMasker.MaskValue(key, value, sensitiveValues)!;
        }

        return sentryEvent;
    }

    // Contexts fora dos tipos do próprio SDK (runtime, os, trace...), que têm esquema fixo e não carregam dado do usuário.
    private static List<KeyValuePair<string, object>> CustomContexts(SentryContexts contexts) =>
        [.. contexts.Where(entry => entry.Value is not null
            && entry.Value.GetType().Namespace?.StartsWith("Sentry", StringComparison.Ordinal) != true)];

    // Mechanism.Data recebe do SDK as chaves string de Exception.Data (SPEC-0015): chave de nome sensível vira ***;
    // os demais valores (texto, dicionários, objetos, structs) são mascarados pelo SentryDataMasker (SPEC-0018).
    // Chaves são mantidas.
    private static void MaskMechanismData(IDictionary<string, object> data, IReadOnlyCollection<string> sensitiveValues)
    {
        foreach (var (key, value) in data.ToList())
        {
            if (SentryDataMasker.MaskValue(key, value, sensitiveValues) is { } masked)
            {
                data[key] = masked;
            }
        }
    }

    // Breadcrumbs vêm dos logs (ILogger) e das requisições: mesmas regras do pipeline do OpenTelemetry. O carimbo de
    // tempo é refeito no momento da cópia (diferença desprezível; o construtor com carimbo não é público). O breadcrumb
    // de log com exceção traz a mensagem dela (exception_message): os valores sensíveis do registro também saem dali.
    private static Breadcrumb MaskBreadcrumb(Breadcrumb breadcrumb)
    {
        var sensitiveValues = MaskingLoggerProvider.CurrentSensitiveValues;
        return new(
            breadcrumb.Message is null ? string.Empty : SensitiveDataMasker.MaskText(breadcrumb.Message, sensitiveValues),
            breadcrumb.Type ?? "default",
            breadcrumb.Data?.ToDictionary(
                entry => entry.Key,
                entry => SensitiveDataMasker.IsSensitiveName(entry.Key) ? SensitiveDataMasker.Mask : SensitiveDataMasker.MaskText(entry.Value, sensitiveValues),
                StringComparer.Ordinal),
            breadcrumb.Category,
            breadcrumb.Level);
    }
}
