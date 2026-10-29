using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Observability;

/// <summary>
/// CT-01 (SPEC-0007): sobre o contrato SPEC-0006@1, um login de prova via <c>POST /api/v1/auth/login</c> produz, no
/// que a Api exporta (exportadores OpenTelemetry em memória acrescentados aos da aplicação), o span HTTP da rota com
/// spans de banco descendentes no mesmo trace e a métrica <c>vitrine.auth.logins{result=success}</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthTelemetryContractTests(PostgresFixture postgres)
{
    private const string LoginRoute = "/api/v1/auth/login";

    [Fact]
    [Trait("spec", "SPEC-0007:CT-01")]
    public async Task Login_de_prova_exporta_trace_com_span_HTTP_da_rota_e_span_de_banco_e_mede_login_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a Api com exportadores de traces e métricas em memória e um usuário existente
        var spans = new LockedCollection<Activity>();
        var metrics = new LockedCollection<MetricSnapshot>();
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString)
            .WithServices(services => services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(spans))
                .WithMetrics(meters => meters.AddInMemoryExporter(metrics)));
        var user = TestUser.New();
        using (var registering = new IdentityApi(factory))
        {
            await registering.RegisterSuccessfullyAsync(user, cancellationToken);
        }

        var successBefore = LoginsSuccess(factory, metrics);

        // Quando um login de prova é feito via POST /api/v1/auth/login
        using (var api = new IdentityApi(factory))
        using (var response = await api.LoginAsync(user.Email, user.Password, cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, response, cancellationToken);
        }

        // Então o trace exportado tem o span HTTP (servidor) da rota de login
        Activity? http = null;
        await Eventually.TrueAsync(
            () => (http = spans.FirstOrDefault(IsLoginServerSpan)) is not null,
            TimeSpan.FromSeconds(10),
            $"span HTTP de POST {LoginRoute} não foi exportado. Spans: {Describe(spans)}",
            cancellationToken);

        // E spans de banco (PostgreSQL) descendentes dele, no mesmo trace_id
        var sameTrace = spans.Where(span => span.TraceId == http!.TraceId).ToList();
        var database = sameTrace.Where(IsDatabaseSpan).ToList();
        Assert.True(database.Count > 0, $"Nenhum span de banco no trace {http!.TraceId} do login. Spans do trace: {Describe(sameTrace)}");
        Assert.All(database, span => Assert.True(
            DescendsFrom(span, http!, sameTrace),
            $"Span de banco \"{span.DisplayName}\" não é descendente do span HTTP do login. Spans do trace: {Describe(sameTrace)}"));

        // E a métrica vitrine.auth.logins{result=success} incrementa
        var successAfter = LoginsSuccess(factory, metrics);
        Assert.True(
            successAfter == successBefore + 1,
            $"vitrine.auth.logins{{result=success}} deveria ir de {successBefore} para {successBefore + 1}; foi para {successAfter}. "
            + $"Métricas exportadas: [{string.Join(", ", metrics.Select(metric => metric.Name).Distinct())}]");
    }

    private static bool IsLoginServerSpan(Activity span) =>
        span.Kind == ActivityKind.Server
        && Equals(span.GetTagItem("http.route"), LoginRoute)
        && Equals(span.GetTagItem("http.request.method"), "POST");

    /// <summary>Span de banco: instrumentação do Npgsql, ou convenção semântica de banco com PostgreSQL.</summary>
    private static bool IsDatabaseSpan(Activity span) =>
        span.Source.Name.StartsWith("Npgsql", StringComparison.Ordinal)
        || string.Equals(span.GetTagItem("db.system")?.ToString(), "postgresql", StringComparison.Ordinal)
        || string.Equals(span.GetTagItem("db.system.name")?.ToString(), "postgresql", StringComparison.Ordinal);

    private static bool DescendsFrom(Activity span, Activity ancestor, IReadOnlyCollection<Activity> trace)
    {
        var parentId = span.ParentSpanId;
        for (var depth = 0; depth < 64 && parentId != default; depth++)
        {
            if (parentId == ancestor.SpanId)
            {
                return true;
            }

            var parent = trace.FirstOrDefault(candidate => candidate.SpanId == parentId);
            if (parent is null)
            {
                return false;
            }

            parentId = parent.ParentSpanId;
        }

        return false;
    }

    /// <summary>Força a coleta das métricas e lê a soma de <c>vitrine.auth.logins{result=success}</c> no último snapshot.</summary>
    private static double LoginsSuccess(ApiFactory factory, LockedCollection<MetricSnapshot> metrics)
    {
        metrics.Clear();
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000), "Coleta de métricas não concluiu.");

        double total = 0;
        foreach (var metric in metrics.Where(metric => metric.Name == AuthMetricsListener.Logins))
        {
            foreach (var point in metric.MetricPoints)
            {
                if (!HasTag(point, "result", "success"))
                {
                    continue;
                }

                total += metric.MetricType switch
                {
                    MetricType.LongSum or MetricType.LongSumNonMonotonic => point.GetSumLong(),
                    MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => point.GetSumDouble(),
                    _ => 0,
                };
            }
        }

        return total;
    }

    private static bool HasTag(MetricPoint point, string key, string value)
    {
        foreach (var tag in point.Tags)
        {
            if (tag.Key == key && Equals(tag.Value?.ToString(), value))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(IEnumerable<Activity> spans) =>
        "[" + string.Join(", ", spans.Select(span => $"{span.Source.Name}:{span.DisplayName}({span.Kind}, trace={span.TraceId})")) + "]";
}
