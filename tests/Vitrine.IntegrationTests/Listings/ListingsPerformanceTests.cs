using System.Diagnostics;
using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-11 — desempenho da listagem: com 10.000 anúncios num workspace, <c>GET /listings?status=active&amp;q=…</c>
/// tem p95 &lt; 150 ms em 20 consultas. O tempo medido é o da requisição inteira no host em memória (consulta + Api),
/// mais estrito que o da consulta sozinha; duas consultas de aquecimento ficam fora da amostra.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ListingsPerformanceTests(PostgresFixture postgres)
{
    private const int Listings = 10_000;
    private const int Samples = 20;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(150);

    [Fact]
    [Trait("spec", "SPEC-0022:IT-11")]
    public async Task Com_10000_anuncios_a_busca_por_trecho_em_active_tem_p95_abaixo_de_150_ms()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = ListingsHost.Create(connectionString, new MercadoLivreItemsDouble());
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado 10.000 anúncios sintéticos num workspace (7.000 active; 1 em cada 10 títulos com o trecho buscado)
        await ListingsDatabase.InsertSyntheticListingsAsync(connectionString, Guid.NewGuid(), owner.WorkspaceId, Listings, "Turquesa", cancellationToken);
        const string Query = "status=active&q=turquesa";

        for (var warmup = 0; warmup < 2; warmup++)
        {
            var page = await api.ListSuccessfullyAsync(Query, cancellationToken);
            Assert.Equal(20, page.Items.Count);
            Assert.All(page.Items, item => Assert.Contains("turquesa", item.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase));
        }

        // Quando a consulta é feita 20 vezes
        var elapsed = new List<TimeSpan>();
        for (var sample = 0; sample < Samples; sample++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await api.ListAsync(Query, cancellationToken);
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            stopwatch.Stop();
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, response, cancellationToken);
            elapsed.Add(stopwatch.Elapsed);
        }

        // Então o p95 é menor que 150 ms
        var sorted = elapsed.Order().ToList();
        var p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
        Assert.True(
            p95 < Budget,
            $"p95 de GET /listings com {Listings} anúncios = {p95.TotalMilliseconds:F1} ms (limite {Budget.TotalMilliseconds} ms). "
            + $"Amostras (ms): {string.Join(", ", sorted.Select(value => value.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}");
    }
}
