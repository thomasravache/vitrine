using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

public sealed class SolutionArchitectureTests
{
    private static readonly string[] SampleAssemblyPrefixes = ["Sample.", "ModuleA.", "ModuleB.", "ModuleC.", "ModuleD.", "Common.Clock"];

    [Fact]
    [Trait("spec", "SPEC-0003:UT-05")]
    public void Assemblies_reais_da_solucao_cumprem_todas_as_regras_de_arquitetura()
    {
        // Dado os assemblies reais da solução (sem as amostras)
        var architecture = SolutionArchitecture.Load();
        var assemblyNames = architecture.Assemblies.Select(a => a.Name).ToList();

        Assert.Contains(assemblyNames, name => name.StartsWith("Vitrine.Api", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assemblyNames,
            name => SampleAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

        // Quando todas as regras de arquitetura rodam
        var failures = RuleEvaluation.Failures(ArchitectureRules.All(architecture), architecture);

        // Então passam
        Assert.True(failures.Count == 0, "Violações de arquitetura:\n" + string.Join('\n', failures));
    }
}
