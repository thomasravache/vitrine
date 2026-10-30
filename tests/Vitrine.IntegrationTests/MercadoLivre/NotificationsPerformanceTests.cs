using System.Globalization;
using System.Net;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// SPEC-0023:IT-05 — o ML exige o 200 em até 500 ms; o contrato pede p95 &lt; 100 ms com 50 POSTs seguidos contra
/// PostgreSQL real. O tempo é medido no cliente de teste (mesmo processo), depois de um aquecimento que não entra na
/// conta (compilação JIT, pool de conexões, primeiro INSERT); o processamento em segundo plano roda junto, como em produção.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationsPerformanceTests(PostgresFixture postgres)
{
    private const int WarmUp = 10;
    private const int Measured = 50;
    private static readonly TimeSpan P95Limit = TimeSpan.FromMilliseconds(100);

    [Fact]
    [Trait("spec", "SPEC-0023:IT-05")]
    public async Task Cinquenta_notificacoes_distintas_em_sequencia_tem_p95_abaixo_de_100_ms()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        using var client = MercadoLivreNotifications.Client(factory);

        // Aquecimento (fora da medição): todas aceitas
        for (var request = 0; request < WarmUp; request++)
        {
            var (status, _) = await MercadoLivreNotifications.TimedPostAsync(
                client, MercadoLivreNotifications.Items(account.Seller.Id, MercadoLivreNotifications.NewItemId()), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, status);
        }

        // Dado PostgreSQL real; quando 50 notificações distintas são enviadas em sequência
        var elapsed = new List<TimeSpan>(Measured);
        for (var request = 0; request < Measured; request++)
        {
            var (status, time) = await MercadoLivreNotifications.TimedPostAsync(
                client, MercadoLivreNotifications.Items(account.Seller.Id, MercadoLivreNotifications.NewItemId()), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, status);
            elapsed.Add(time);
        }

        // Então o p95 do tempo de resposta é < 100 ms (e as 60 foram gravadas)
        elapsed.Sort();
        var p95 = elapsed[(int)Math.Ceiling(0.95 * Measured) - 1];
        Assert.True(
            p95 < P95Limit,
            $"p95 = {p95.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms (limite {P95Limit.TotalMilliseconds} ms); "
            + "tempos (ms): " + string.Join(", ", elapsed.Select(time => time.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture))));
        Assert.Equal(WarmUp + Measured, await MercadoLivreNotifications.CountAsync(connectionString, cancellationToken));
    }
}
