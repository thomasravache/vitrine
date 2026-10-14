using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;

namespace Vitrine.ArchitectureTests.Support;

internal static class RuleEvaluation
{
    /// <summary>Avalia as regras e devolve a descrição de cada resultado reprovado.</summary>
    public static List<string> Failures(IEnumerable<IArchRule> rules, Architecture architecture) =>
        rules
            .SelectMany(rule => rule.Evaluate(architecture))
            .Where(result => !result.Passed)
            .Select(result => result.Description)
            .ToList();
}
