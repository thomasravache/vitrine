using System.Diagnostics;
using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Observability;

/// <summary>
/// IT-04 (SPEC-0007): destino de telemetria fora do ar não afeta a aplicação. Compara duas Apis idênticas — linha de
/// base sem exportador OTLP e outra com <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> numa porta fechada — com exportação
/// agendada a cada ~100 ms, para que as tentativas de envio (falhas) aconteçam durante a medição.
/// </summary>
/// <remarks>
/// Ruído: requisições em memória levam frações de milissegundo, e um p95 isolado oscila mais que 10% por GC/JIT/CI.
/// Por isso: aquecimento antes de medir, <see cref="Rounds"/> rodadas alternando a ordem das Apis, comparação das
/// medianas dos p95 e uma folga absoluta mínima de <see cref="AbsoluteSlackMs"/> ms somada aos +10%. Um exportador
/// síncrono (ex.: timeout de conexão de segundos no caminho da requisição) estoura essa folga por ordens de grandeza.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class TelemetryDestinationDownTests(PostgresFixture postgres)
{
    private const int RequestsPerRound = 50;
    private const int Rounds = 3;
    private const int WarmupRequests = 20;
    private const double RelativeTolerance = 1.10;
    private const double AbsoluteSlackMs = 5;

    // Rota anônima, sem banco, instrumentada (gera span, métricas HTTP e logs do pipeline).
    private const string ProbePath = IdentityApi.AntiforgeryPath;

    [Fact]
    [Trait("spec", "SPEC-0007:IT-04")]
    public async Task Com_endpoint_OTLP_inacessivel_50_requisicoes_respondem_e_p95_fica_dentro_de_10_por_cento_da_linha_de_base()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado uma Api de linha de base (sem OTLP) e outra com OTEL_EXPORTER_OTLP_ENDPOINT numa porta fechada
        var unreachable = ClosedPort.NewLocalUrl();
        await using var baseline = WithFastExport(IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)))
            .WithSetting("OTEL_EXPORTER_OTLP_ENDPOINT", string.Empty);
        await using var unreachableOtlp = WithFastExport(IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)))
            .WithSetting("OTEL_EXPORTER_OTLP_ENDPOINT", unreachable.ToString());
        using var baselineClient = baseline.CreateClient();
        using var otlpClient = unreachableOtlp.CreateClient();
        await MeasureAsync(baselineClient, WarmupRequests, cancellationToken);
        await MeasureAsync(otlpClient, WarmupRequests, cancellationToken);

        // Quando 50 requisições são feitas (por rodada, alternando a ordem das Apis)
        var baselineP95 = new List<double>();
        var otlpP95 = new List<double>();
        for (var round = 0; round < Rounds; round++)
        {
            if (round % 2 == 0)
            {
                baselineP95.Add(P95(await MeasureAsync(baselineClient, RequestsPerRound, cancellationToken)));
                otlpP95.Add(P95(await MeasureAsync(otlpClient, RequestsPerRound, cancellationToken)));
            }
            else
            {
                otlpP95.Add(P95(await MeasureAsync(otlpClient, RequestsPerRound, cancellationToken)));
                baselineP95.Add(P95(await MeasureAsync(baselineClient, RequestsPerRound, cancellationToken)));
            }

            // Dá tempo ao exportador de tentar (e falhar) entre as rodadas.
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        // Então todas responderam com sucesso (MeasureAsync exige 2xx) e o p95 fica em até +10% da linha de base
        var baselineMedian = Median(baselineP95);
        var otlpMedian = Median(otlpP95);
        var limit = (baselineMedian * RelativeTolerance) + AbsoluteSlackMs;
        Assert.True(
            otlpMedian <= limit,
            $"p95 com destino OTLP fora ({otlpMedian:F2} ms; rodadas: {string.Join(", ", otlpP95.Select(value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}) "
            + $"passou do limite {limit:F2} ms (linha de base {baselineMedian:F2} ms × 1,10 + {AbsoluteSlackMs} ms).");
    }

    /// <summary>Exportação agendada curta (traces, logs, métricas) para as falhas de envio ocorrerem durante a medição.</summary>
    private static ApiFactory WithFastExport(ApiFactory factory) => factory
        .WithSetting("OTEL_BSP_SCHEDULE_DELAY", "100")
        .WithSetting("OTEL_BLRP_SCHEDULE_DELAY", "100")
        .WithSetting("OTEL_METRIC_EXPORT_INTERVAL", "200");

    private static async Task<List<double>> MeasureAsync(HttpClient client, int requests, CancellationToken cancellationToken)
    {
        var durations = new List<double>(requests);
        for (var i = 0; i < requests; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(new Uri(ProbePath, UriKind.Relative), cancellationToken);
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.True(
                response.StatusCode == HttpStatusCode.NoContent,
                $"GET {ProbePath} deveria responder 204; respondeu {(int)response.StatusCode} {response.StatusCode}.");
        }

        return durations;
    }

    private static double P95(List<double> durations)
    {
        var sorted = durations.Order().ToList();
        var index = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
