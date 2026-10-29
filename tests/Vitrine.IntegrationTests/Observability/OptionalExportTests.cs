using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Vitrine.IntegrationTests.Observability;

/// <summary>
/// IT-06 (SPEC-0007): exportação para Grafana Cloud e Sentry é opcional. Sem as variáveis de produção a Api sobe e
/// atende sem erro de configuração, e a telemetria segue só para o dashboard do Aspire.
/// </summary>
/// <remarks>
/// No ambiente local o AppHost entrega a cada projeto o endpoint OTLP do dashboard (o
/// <c>DOTNET_DASHBOARD_OTLP_ENDPOINT_URL</c> do AppHost) como <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, sem
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c> nem <c>Sentry__Dsn</c>. O teste reproduz isso com um dashboard falso (WireMock
/// recebendo OTLP/HTTP) e verifica que os traces chegam a ele — de forma observável, sem depender do AppHost.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class OptionalExportTests(PostgresFixture postgres)
{
    private const string TracesPath = "/v1/traces";

    [Fact]
    [Trait("spec", "SPEC-0007:IT-06")]
    public async Task Sem_variaveis_OTLP_nem_Sentry_a_Api_sobe_e_atende_sem_erro_de_configuracao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o host sem OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_HEADERS nem Sentry__Dsn (vazios mesmo que o
        // ambiente do processo de teste os tenha) e um capturador de todos os logs do host
        var logs = new RecordingLoggerProvider();
        await using var factory = WithoutProductionTelemetry(IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)))
            .WithSetting("OTEL_EXPORTER_OTLP_ENDPOINT", string.Empty)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        // Quando a Api sobe e atende requisições
        using var client = factory.CreateClient();
        using (var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        using (var antiforgery = await client.GetAsync(new Uri(IdentityApi.AntiforgeryPath, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, antiforgery.StatusCode);
        }

        // Então nenhum erro/aviso de configuração de telemetria foi registrado (categoria ou conteúdo sobre
        // OpenTelemetry/OTLP/Sentry). Erros alheios à telemetria (ex.: o EF consultando o histórico de migrações num
        // banco recém-criado) não entram; falha de subida ou 5xx já derrubariam o teste acima.
        var problems = logs.Records
            .Where(log => log.Level >= LogLevel.Warning && IsAboutTelemetry(log))
            .ToList();
        Assert.True(problems.Count == 0, "Erros/avisos de configuração na subida sem telemetria de produção:\n" + string.Join('\n', problems));
    }

    [Fact]
    [Trait("spec", "SPEC-0007:IT-06")]
    public async Task Sem_variaveis_de_producao_os_traces_vao_para_o_endpoint_OTLP_do_dashboard_do_Aspire()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o host como o AppHost o sobe localmente: OTLP apontando para o dashboard (falso), sem cabeçalhos do
        // Grafana Cloud nem DSN do Sentry
        var factory = WithoutProductionTelemetry(IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)));
        await using var _ = factory;
        var dashboard = factory.ExternalServices;
        dashboard.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        factory
            .WithSetting("OTEL_EXPORTER_OTLP_ENDPOINT", dashboard.Url)
            .WithSetting("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithSetting("OTEL_BSP_SCHEDULE_DELAY", "100");

        // Quando a Api sobe e atende requisições
        using var client = factory.CreateClient();
        using (var antiforgery = await client.GetAsync(new Uri(IdentityApi.AntiforgeryPath, UriKind.Relative), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, antiforgery.StatusCode);
        }

        // Então o endpoint OTLP do dashboard recebe os traces
        await Eventually.TrueAsync(
            () => dashboard.LogEntries.Any(entry => entry.RequestMessage?.Path == TracesPath),
            TimeSpan.FromSeconds(30),
            $"O dashboard (OTLP em {dashboard.Url}) não recebeu POST {TracesPath}. Recebeu: "
            + string.Join(", ", dashboard.LogEntries.Select(entry => entry.RequestMessage?.Path)),
            cancellationToken);
    }

    /// <summary>Sem as variáveis de produção (Grafana Cloud e Sentry), mesmo que o processo de teste as tenha.</summary>
    private static ApiFactory WithoutProductionTelemetry(ApiFactory factory) => factory
        .WithSetting("OTEL_EXPORTER_OTLP_HEADERS", string.Empty)
        .WithSetting("Sentry:Dsn", string.Empty)
        .WithSetting("SENTRY_DSN", string.Empty);

    private static readonly string[] TelemetryMarkers = ["OpenTelemetry", "Otlp", "OTEL_", "Sentry", "Dsn", "Observability"];

    private static bool IsAboutTelemetry(RecordedLog log) =>
        TelemetryMarkers.Any(marker =>
            log.Category.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || log.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || (log.Exception?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false));
}
