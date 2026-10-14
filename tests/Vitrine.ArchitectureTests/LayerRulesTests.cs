using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

public sealed class LayerRulesTests
{
    [Fact]
    [Trait("spec", "SPEC-0003:UT-03")]
    public void Regra_de_camadas_falha_apontando_a_dependencia_quando_Domain_de_amostra_referencia_Infrastructure()
    {
        // Dado assemblies de amostra em que Sample.Domain referencia Sample.Infrastructure
        var architecture = SampleArchitectures.LayerViolation;

        // Quando a regra de camadas roda
        var failures = RuleEvaluation.Failures(ArchitectureRules.LayerRules(architecture), architecture);

        // Então ela falha apontando a dependência proibida
        Assert.True(failures.Count > 0, "A regra de camadas passou com Sample.Domain dependendo de Sample.Infrastructure.");
        Assert.True(
            failures.Any(d => d.Contains("Sample.Domain", StringComparison.Ordinal)
                && d.Contains("Sample.Infrastructure", StringComparison.Ordinal)),
            "A falha não aponta a dependência Sample.Domain -> Sample.Infrastructure. Falhas:\n"
                + string.Join('\n', failures));
    }
}
