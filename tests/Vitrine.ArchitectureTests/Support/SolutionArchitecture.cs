using ArchUnitNET.Domain;

namespace Vitrine.ArchitectureTests.Support;

/// <summary>
/// Arquitetura dos assemblies reais da solução (host, ServiceDefaults, Common e módulos em src/),
/// sem os assemblies de amostra de <c>Samples/</c>.
/// </summary>
public static class SolutionArchitecture
{
    public static Architecture Load() =>
        throw new NotImplementedException("SPEC-0003: carga dos assemblies da solução ainda não implementada.");
}
