using Vitrine.Listings.Domain.Listings;

namespace Vitrine.UnitTests.Listings;

/// <summary>SPEC-0022:UT-03 — leitura do ML mais antiga que a cópia gravada é ignorada; igual ou mais nova é aplicada.</summary>
public sealed class ListingUpdatePolicyTests
{
    private static readonly DateTimeOffset Stored = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("spec", "SPEC-0022:UT-03")]
    [InlineData(-3_600_000)]
    [InlineData(-1)]
    public void Found_com_LastUpdated_anterior_ao_gravado_e_ignorado(int deltaMilliseconds) =>
        Assert.False(ListingUpdatePolicy.ShouldApply(Stored, Stored.AddMilliseconds(deltaMilliseconds)));

    [Theory]
    [Trait("spec", "SPEC-0022:UT-03")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3_600_000)]
    public void Found_com_LastUpdated_igual_ou_posterior_ao_gravado_e_aplicado(int deltaMilliseconds) =>
        Assert.True(ListingUpdatePolicy.ShouldApply(Stored, Stored.AddMilliseconds(deltaMilliseconds)));

    [Fact]
    [Trait("spec", "SPEC-0022:UT-03")]
    public void O_mesmo_instante_em_outro_fuso_conta_como_igual()
    {
        var sameInstant = Stored.ToOffset(TimeSpan.FromHours(-3));

        Assert.True(ListingUpdatePolicy.ShouldApply(Stored, sameInstant));
        Assert.False(ListingUpdatePolicy.ShouldApply(Stored, sameInstant.AddSeconds(-1)));
    }
}
