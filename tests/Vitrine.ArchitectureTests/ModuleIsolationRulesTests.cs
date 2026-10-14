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

    [Fact]
    [Trait("spec", "SPEC-0003:UT-04")]
    public void Regra_de_ciclos_falha_quando_dois_modulos_dependem_um_do_outro_mesmo_pelos_contratos_publicos()
    {
        // Dado assemblies de amostra em que ModuleC usa o contrato público de ModuleD e ModuleD usa o de ModuleC
        var architecture = SampleArchitectures.ModuleCycle;

        // (o isolamento sozinho não pega: só contratos públicos são usados)
        var isolationFailures = RuleEvaluation.Failures(ArchitectureRules.ModuleIsolationRules(architecture), architecture);
        Assert.True(isolationFailures.Count == 0, "Amostra inválida: o ciclo deveria usar só contratos públicos.\n"
            + string.Join('\n', isolationFailures));

        // Quando a regra de ausência de ciclos roda
        var failures = RuleEvaluation.Failures(ArchitectureRules.ModuleCycleRules(architecture), architecture);

        // Então ela falha apontando os dois módulos do ciclo
        Assert.True(failures.Count > 0, "A regra de ciclos passou com ModuleC e ModuleD dependendo um do outro.");
        Assert.True(
            failures.Any(d => d.Contains("ModuleC", StringComparison.Ordinal)
                && d.Contains("ModuleD", StringComparison.Ordinal)),
            "A falha não aponta o ciclo ModuleC <-> ModuleD. Falhas:\n" + string.Join('\n', failures));
    }

    [Fact]
    [Trait("spec", "SPEC-0003:UT-04")]
    public void Regra_de_Common_falha_quando_Common_referencia_um_modulo()
    {
        // Dado assemblies de amostra em que Common.Clock referencia ModuleB.Application
        var architecture = SampleArchitectures.CommonViolation;

        // Quando a regra de Common sem módulos roda
        var failures = RuleEvaluation.Failures(ArchitectureRules.CommonRules(architecture), architecture);

        // Então ela falha apontando a dependência proibida
        Assert.True(failures.Count > 0, "A regra de Common passou com Common.Clock dependendo de ModuleB.Application.");
        Assert.True(
            failures.Any(d => d.Contains("Common.Clock", StringComparison.Ordinal)
                && d.Contains("ModuleB.Application", StringComparison.Ordinal)),
            "A falha não aponta a dependência Common.Clock -> ModuleB.Application. Falhas:\n"
                + string.Join('\n', failures));
    }
}
