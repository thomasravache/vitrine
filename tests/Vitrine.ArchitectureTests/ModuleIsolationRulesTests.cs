using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

public sealed class ModuleIsolationRulesTests
{
    [Fact]
    [Trait("spec", "SPEC-0003:UT-04")]
    public void Regra_de_isolamento_falha_quando_Application_de_um_modulo_referencia_Application_de_outro()
    {
        // Dado assemblies de amostra em que ModuleA.Application referencia ModuleB.Application
        var architecture = SampleArchitectures.ModuleViolation;

        // Quando a regra de isolamento de módulos roda
        var failures = RuleEvaluation.Failures(ArchitectureRules.ModuleIsolationRules(architecture), architecture);

        // Então ela falha apontando a dependência entre os módulos
        Assert.True(failures.Count > 0, "A regra de isolamento passou com ModuleA.Application dependendo de ModuleB.Application.");
        Assert.True(
            failures.Any(d => d.Contains("ModuleA.Application", StringComparison.Ordinal)
                && d.Contains("ModuleB.Application", StringComparison.Ordinal)),
            "A falha não aponta a dependência ModuleA.Application -> ModuleB.Application. Falhas:\n"
                + string.Join('\n', failures));
    }
}
