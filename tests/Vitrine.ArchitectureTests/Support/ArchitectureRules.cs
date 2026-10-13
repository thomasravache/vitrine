using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;

namespace Vitrine.ArchitectureTests.Support;

/// <summary>
/// Regras de fronteira da ADR-0001, aplicadas por convenção de nomes a qualquer arquitetura carregada.
/// Módulo e camada são reconhecidos pelo namespace <c>…&lt;Modulo&gt;.&lt;Camada&gt;[.…]</c>, com camada em
/// Domain, Application, Infrastructure, Presentation, IntegrationEvents ou PublicApi
/// (ex.: <c>Vitrine.Modules.Identity.Domain</c>, <c>Sample.Domain</c>).
/// </summary>
public static class ArchitectureRules
{
    /// <summary>
    /// Camadas dentro de um módulo: Domain não depende de Application, Infrastructure nem Presentation;
    /// Application não depende de Infrastructure nem Presentation.
    /// </summary>
    public static IReadOnlyList<IArchRule> LayerRules(Architecture architecture) =>
        throw new NotImplementedException("SPEC-0003: regras de camadas da ADR-0001 ainda não implementadas.");

    /// <summary>
    /// Isolamento: nenhum tipo de um módulo depende de Domain, Application, Infrastructure ou Presentation
    /// de outro módulo (só do contrato público: IntegrationEvents e PublicApi).
    /// </summary>
    public static IReadOnlyList<IArchRule> ModuleIsolationRules(Architecture architecture) =>
        throw new NotImplementedException("SPEC-0003: regras de isolamento entre módulos ainda não implementadas.");

    /// <summary>Não há ciclos de dependência entre módulos.</summary>
    public static IReadOnlyList<IArchRule> ModuleCycleRules(Architecture architecture) =>
        throw new NotImplementedException("SPEC-0003: regra de ausência de ciclos ainda não implementada.");

    /// <summary><c>Common</c> (building blocks) não depende de módulos.</summary>
    public static IReadOnlyList<IArchRule> CommonRules(Architecture architecture) =>
        throw new NotImplementedException("SPEC-0003: regra de Common sem módulos ainda não implementada.");

    /// <summary>Todas as regras da ADR-0001.</summary>
    public static IReadOnlyList<IArchRule> All(Architecture architecture) =>
        [
            .. LayerRules(architecture),
            .. ModuleIsolationRules(architecture),
            .. ModuleCycleRules(architecture),
            .. CommonRules(architecture),
        ];
}
