using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

/// <summary>
/// SPEC-0022:UT-04 — fronteiras do módulo Listings (ADR-0001, ADR-0011): camadas e isolamento, só contratos públicos de
/// outros módulos (<c>MercadoLivre.PublicApi</c>, <c>MercadoLivre.IntegrationEvents</c>, <c>Identity.PublicApi</c>) e
/// schema <c>listings</c> próprio.
/// </summary>
public sealed class ListingsModuleArchitectureTests
{
    private const string Module = "Vitrine.Listings";
    private const string Schema = "listings";

    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Presentation", "IntegrationEvents"];

    // IntegrationEvents nasce vazio (reservado — SPEC-0022).
    private static readonly string[] LayersWithTypes = ["Domain", "Application", "Infrastructure", "Presentation"];

    private static readonly string[] AllowedForeignModuleAssemblies =
        ["Vitrine.MercadoLivre.PublicApi", "Vitrine.MercadoLivre.IntegrationEvents", "Vitrine.Identity.PublicApi"];

    [Fact]
    [Trait("spec", "SPEC-0022:UT-04")]
    public void Assemblies_do_modulo_Listings_respeitam_camadas_isolamento_e_ausencia_de_ciclos()
    {
        // Dado os assemblies reais da solução, com os cinco projetos do módulo Listings
        var architecture = SolutionArchitecture.Load();
        var assemblyNames = architecture.Assemblies.Select(assembly => assembly.Name.Split(',')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var layer in Layers)
        {
            Assert.True(assemblyNames.Contains($"{Module}.{layer}"), $"Assembly {Module}.{layer} não está na arquitetura carregada.");
        }

        foreach (var layer in LayersWithTypes)
        {
            Assert.True(
                architecture.Types.Any(type => type.Namespace.FullName.StartsWith($"{Module}.{layer}", StringComparison.Ordinal)),
                $"Nenhum tipo no namespace {Module}.{layer}: as regras não teriam o que verificar.");
        }

        // Quando as regras de arquitetura (camadas, isolamento, ciclos, Common) rodam
        var failures = RuleEvaluation.Failures(ArchitectureRules.All(architecture), architecture)
            .Where(failure => failure.Contains(Module, StringComparison.Ordinal))
            .ToList();

        // Então o módulo Listings não viola nenhuma
        Assert.True(failures.Count == 0, "Violações de arquitetura no módulo Listings:\n" + string.Join('\n', failures));
    }

    [Fact]
    [Trait("spec", "SPEC-0022:UT-04")]
    public void Listings_so_referencia_os_contratos_publicos_do_MercadoLivre_e_do_Identity()
    {
        // Dado os assemblies do módulo Listings
        var references = Layers
            .Select(layer => SolutionAssembly($"{Module}.{layer}"))
            .SelectMany(assembly => assembly.GetReferencedAssemblies().Select(reference => (From: assembly.GetName().Name, To: reference.Name ?? string.Empty)))
            .ToList();

        // Quando as referências a outros módulos são lidas
        var foreign = references
            .Where(reference => reference.To.StartsWith("Vitrine.", StringComparison.Ordinal)
                && !reference.To.StartsWith($"{Module}.", StringComparison.Ordinal)
                && !reference.To.StartsWith("Vitrine.Common.", StringComparison.Ordinal))
            .ToList();
        var violations = foreign
            .Where(reference => !AllowedForeignModuleAssemblies.Contains(reference.To, StringComparer.Ordinal))
            .Select(reference => $"{reference.From} -> {reference.To}")
            .ToList();

        // Então só há MercadoLivre.PublicApi, MercadoLivre.IntegrationEvents e Identity.PublicApi
        Assert.True(violations.Count == 0, "Listings referencia assemblies internos de outros módulos:\n" + string.Join('\n', violations));

        // E o módulo de fato consome os três contratos (o gateway, os eventos da conta e o workspace do usuário)
        foreach (var contract in AllowedForeignModuleAssemblies)
        {
            Assert.True(
                foreign.Any(reference => reference.To == contract),
                $"Nenhum assembly do Listings referencia {contract} (contratos consumidos: SPEC-0020@1, SPEC-0021@1, SPEC-0023@1).");
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0022:UT-04")]
    public void DbContexts_do_modulo_Listings_mapeiam_somente_o_schema_listings()
    {
        // Dado os DbContexts do módulo Listings
        var contexts = Layers
            .Select(layer => SolutionAssembly($"{Module}.{layer}"))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(type))
            .ToList();
        Assert.True(contexts.Count > 0, $"O módulo Listings não tem DbContext (schema \"{Schema}\" próprio — ADR-0011).");

        foreach (var contextType in contexts)
        {
            // Quando o modelo do EF Core é montado (sem conexão com o banco)
            using var context = CreateContext(contextType);
            var model = context.GetService<IDesignTimeModel>().Model;

            // Então toda tabela, visão e sequência fica no schema "listings" (o outbox do Wolverine é do host)
            var outside = model.GetEntityTypes()
                .Where(entity => entity.GetTableName() is not null || entity.GetViewName() is not null)
                .Where(entity => entity.ClrType.Namespace?.StartsWith("Wolverine", StringComparison.Ordinal) != true)
                .Where(entity => (entity.GetSchema() ?? entity.GetViewSchema() ?? model.GetDefaultSchema()) != Schema)
                .Select(entity => $"{entity.DisplayName()} -> {entity.GetSchema() ?? entity.GetViewSchema() ?? model.GetDefaultSchema() ?? "(padrão do banco)"}")
                .Concat(model.GetSequences()
                    .Where(sequence => (sequence.Schema ?? model.GetDefaultSchema()) != Schema)
                    .Select(sequence => $"sequência {sequence.Name} -> {sequence.Schema ?? "(padrão do banco)"}"))
                .ToList();
            Assert.True(outside.Count == 0, $"{contextType.Name} mapeia objetos fora do schema \"{Schema}\":\n" + string.Join('\n', outside));

            // E as tabelas do contrato existem
            var tables = model.GetEntityTypes().Select(entity => entity.GetTableName()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            Assert.Contains("listings", tables);
            Assert.Contains("sync_states", tables);
        }
    }

    private static Assembly SolutionAssembly(string name)
    {
        _ = SolutionArchitecture.Load();
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == name)
            ?? Assembly.Load(name);
    }

    /// <summary>Instancia o DbContext pelo construtor do EF Core (<c>DbContextOptions&lt;T&gt;</c>) com o provedor PostgreSQL.</summary>
    private static DbContext CreateContext(Type contextType)
    {
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        builder.UseNpgsql("Host=localhost;Database=architecture_tests");
        var constructor = contextType.GetConstructors().OrderBy(candidate => candidate.GetParameters().Length).First();
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsInstanceOfType(builder.Options)
                ? builder.Options
                : throw new InvalidOperationException($"{contextType.Name}: parâmetro de construtor não suportado ({parameter.ParameterType})."))
            .ToArray();
        return (DbContext)constructor.Invoke(arguments);
    }
}
