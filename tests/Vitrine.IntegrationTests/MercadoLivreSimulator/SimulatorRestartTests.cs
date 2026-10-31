using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0031:IT-01 — os IDs de <c>POST /__sim/sellers</c> não se repetem entre execuções do simulador: o banco do Vitrine
/// persiste entre deploys, e um ID repetido colide com uma conta já conectada (409 <c>ML_ACCOUNT_IN_USE</c>).
/// </summary>
public sealed class SimulatorRestartTests
{
    private const int SellersPerRun = 3;

    [Fact]
    [Trait("spec", "SPEC-0031:IT-01")]
    public async Task Ids_de_vendedores_criados_nao_se_repetem_entre_reinicios_do_simulador()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o simulador iniciado e usado (1ª execução)
        var first = await CreateSellersAsync(clockAdvance: TimeSpan.Zero, cancellationToken);

        // Quando ele é reiniciado depois (2ª execução, um minuto mais tarde no relógio do simulador) e cria vendedores
        var second = await CreateSellersAsync(clockAdvance: TimeSpan.FromMinutes(1), cancellationToken);

        // Então nenhum ID se repete entre as execuções
        var repeated = first.Intersect(second).ToList();
        Assert.True(repeated.Count == 0, $"IDs repetidos depois do reinício: [{string.Join(", ", repeated)}] (1ª: [{string.Join(", ", first)}]; 2ª: [{string.Join(", ", second)}])");

        // E todos continuam >= 3000001000, maiores que Int32 e crescentes dentro de cada execução
        Assert.All(first.Concat(second), id =>
        {
            Assert.True(id >= 3_000_001_000, $"id {id} < 3000001000");
            Assert.True(id > int.MaxValue, $"id {id} cabe em Int32");
        });
        Assert.Equal(first.Order(), first);
        Assert.Equal(second.Order(), second);
        Assert.Equal(SellersPerRun, first.Distinct().Count());
        Assert.Equal(SellersPerRun, second.Distinct().Count());
    }

    /// <summary>Sobe um host novo (estado zerado, como um reinício), avança o relógio dele e cria vendedores.</summary>
    private static async Task<List<long>> CreateSellersAsync(TimeSpan clockAdvance, CancellationToken cancellationToken)
    {
        await using var host = new SimulatorHost();
        host.Clock.Advance(clockAdvance);
        using var client = host.NewClient();
        var ids = new List<long>();
        for (var i = 0; i < SellersPerRun; i++)
        {
            ids.Add((await client.CreateSellerAsync(cancellationToken: cancellationToken)).Id);
        }

        return ids;
    }
}
