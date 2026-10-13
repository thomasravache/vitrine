using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Slices;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Vitrine.ArchitectureTests.Support;

/// <summary>
/// Regras de fronteira da ADR-0001, aplicadas por convenção de nomes a qualquer arquitetura carregada.
/// Módulo e camada são reconhecidos pelo namespace <c>…&lt;Modulo&gt;.&lt;Camada&gt;[.…]</c>, com camada em
/// Domain, Application, Infrastructure, Presentation, IntegrationEvents ou PublicApi
/// (ex.: <c>Vitrine.Modules.Identity.Domain</c>, <c>Sample.Domain</c>).
/// </summary>
public static class ArchitectureRules
{
    private const string Domain = "Domain";
    private const string Application = "Application";
    private const string Infrastructure = "Infrastructure";
    private const string Presentation = "Presentation";

    /// <summary>Camadas internas de um módulo; o contrato público (IntegrationEvents, PublicApi) fica de fora.</summary>
    private static readonly string[] InternalLayers = [Domain, Application, Infrastructure, Presentation];

    // Prefixo mais curto antes do primeiro segmento de camada: "Vitrine.Modules.Identity.Domain.Users" → (Vitrine.Modules.Identity, Domain).
    private static readonly Regex ModuleNamespace = new(
        @"^(?<module>.+?)\.(?<layer>Domain|Application|Infrastructure|Presentation|IntegrationEvents|PublicApi)(\.|$)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Regex CommonNamespace = new(
        @"(^|\.)Common(\.|$)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Camadas dentro de um módulo: Domain não depende de Application, Infrastructure nem Presentation;
    /// Application não depende de Infrastructure nem Presentation.
    /// </summary>
    public static IReadOnlyList<IArchRule> LayerRules(Architecture architecture) =>
        [
            .. Modules(architecture).SelectMany(module => new[]
            {
                LayerMustNotDependOn(module, Domain, Application, Infrastructure, Presentation),
                LayerMustNotDependOn(module, Application, Infrastructure, Presentation),
            }),
        ];

    /// <summary>
    /// Isolamento: nenhum tipo de um módulo depende de Domain, Application, Infrastructure ou Presentation
    /// de outro módulo (só do contrato público: IntegrationEvents e PublicApi).
    /// </summary>
    public static IReadOnlyList<IArchRule> ModuleIsolationRules(Architecture architecture)
    {
        var modules = Modules(architecture);
        return
        [
            .. modules
                .Select(module => (module, others: modules.Where(other => other != module).ToList()))
                .Where(pair => pair.others.Count > 0)
                .Select(pair => (IArchRule)Types().That().ResideInNamespaceMatching(ModulePattern(pair.module))
                    .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(
                        AnyOf(pair.others.Select(other => LayerPattern(other, InternalLayers)))))
                    .Because($"o módulo {pair.module} só pode usar o contrato público (IntegrationEvents/PublicApi) de outros módulos (ADR-0001)")
                    .WithoutRequiringPositiveResults()),
        ];
    }

    /// <summary>Não há ciclos de dependência entre módulos.</summary>
    public static IReadOnlyList<IArchRule> ModuleCycleRules(Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);

        var slices = new SliceRuleCreator();
        slices.SetSliceAssignment(new SliceAssignment(
            type => ModuleOf(type.Namespace.FullName) is { } module ? SliceIdentifier.Of(module) : SliceIdentifier.Ignore(),
            "por módulo (convenção <Modulo>.<Camada>)"));
        return [new GivenSlices(slices).Should().BeFreeOfCycles()];
    }

    /// <summary><c>Common</c> (building blocks) não depende de módulos.</summary>
    public static IReadOnlyList<IArchRule> CommonRules(Architecture architecture)
    {
        var modules = Modules(architecture);
        if (modules.Count == 0)
        {
            return [];
        }

        return
        [
            Types().That().ResideInNamespaceMatching(CommonNamespace.ToString())
                .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyOf(modules.Select(ModulePattern))))
                .Because("Common (building blocks) não referencia módulos (ADR-0001)")
                .WithoutRequiringPositiveResults(),
        ];
    }

    /// <summary>Todas as regras da ADR-0001.</summary>
    public static IReadOnlyList<IArchRule> All(Architecture architecture) =>
        [
            .. LayerRules(architecture),
            .. ModuleIsolationRules(architecture),
            .. ModuleCycleRules(architecture),
            .. CommonRules(architecture),
        ];

    private static ArchRule<IType> LayerMustNotDependOn(string module, string layer, params string[] forbidden) =>
        Types().That().ResideInNamespaceMatching(LayerPattern(module, layer))
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(LayerPattern(module, forbidden)))
            .Because($"{layer} não depende de {string.Join(", ", forbidden)} (ADR-0001)")
            .WithoutRequiringPositiveResults();

    /// <summary>Módulos presentes na arquitetura, pela convenção de namespace (Common nunca é módulo).</summary>
    private static List<string> Modules(Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);

        return architecture.Types
            .Select(type => ModuleOf(type.Namespace.FullName))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string? ModuleOf(string @namespace)
    {
        var match = ModuleNamespace.Match(@namespace);
        if (!match.Success)
        {
            return null;
        }

        var module = match.Groups["module"].Value;
        return CommonNamespace.IsMatch(module) ? null : module;
    }

    private static string ModulePattern(string module) => $@"^{Regex.Escape(module)}\.";

    private static string LayerPattern(string module, params string[] layers) =>
        $@"^{Regex.Escape(module)}\.({string.Join('|', layers)})(\.|$)";

    private static string AnyOf(IEnumerable<string> patterns) => string.Join('|', patterns.Select(p => $"({p})"));
}
