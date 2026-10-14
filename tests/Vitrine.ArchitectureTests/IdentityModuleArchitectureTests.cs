using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

/// <summary>Fronteiras do módulo Identity (ADR-0001, ADR-0011): camadas, isolamento e schema próprio.</summary>
public sealed class IdentityModuleArchitectureTests
{
    private const string Module = "Vitrine.Identity";
    private const string Schema = "identity";

    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Presentation", "IntegrationEvents"];

    [Fact]
    [Trait("spec", "SPEC-0006:UT-04")]
    public void Assemblies_do_modulo_Identity_respeitam_camadas_isolamento_e_ausencia_de_ciclos()
    {
        // Dado os assemblies reais da solução, com os cinco projetos do módulo Identity
        var architecture = SolutionArchitecture.Load();
        var assemblyNames = architecture.Assemblies.Select(assembly => assembly.Name).ToList();
        foreach (var layer in Layers)
        {
            Assert.True(
                assemblyNames.Any(name => name.StartsWith($"{Module}.{layer},", StringComparison.Ordinal)
                    || name.Equals($"{Module}.{layer}", StringComparison.Ordinal)),
                $"Assembly {Module}.{layer} não está na arquitetura carregada.");
            Assert.True(
                architecture.Types.Any(type => type.Namespace.FullName.StartsWith($"{Module}.{layer}", StringComparison.Ordinal)),
                $"Nenhum tipo no namespace {Module}.{layer}: as regras não teriam o que verificar.");
        }

        // Quando as regras de arquitetura (camadas, isolamento, ciclos, Common) rodam
        var failures = RuleEvaluation.Failures(ArchitectureRules.All(architecture), architecture)
            .Where(failure => failure.Contains(Module, StringComparison.Ordinal))
            .ToList();

        // Então o módulo Identity não viola nenhuma
        Assert.True(failures.Count == 0, "Violações de arquitetura no módulo Identity:\n" + string.Join('\n', failures));
    }

    [Fact]
    [Trait("spec", "SPEC-0006:UT-04")]
    public void DbContexts_do_modulo_Identity_mapeiam_somente_o_schema_identity()
    {
        // Dado os DbContexts do módulo Identity
        var contexts = Layers
            .Select(layer => Assembly.Load($"{Module}.{layer}"))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(type))
            .ToList();
        Assert.True(contexts.Count > 0, $"O módulo Identity não tem DbContext (schema \"{Schema}\" próprio — ADR-0011).");

        foreach (var contextType in contexts)
        {
            // Quando o modelo do EF Core é montado (sem conexão com o banco)
            using var context = CreateContext(contextType);
            var model = context.GetService<IDesignTimeModel>().Model;

            // Então toda tabela, visão e sequência fica no schema "identity" (um módulo não toca outro schema);
            // o armazenamento de envelopes do Wolverine (outbox, ADR-0014) é infraestrutura do host, não de outro módulo
            var outside = model.GetEntityTypes()
                .Where(entity => entity.GetTableName() is not null || entity.GetViewName() is not null)
                .Where(entity => entity.ClrType.Namespace?.StartsWith("Wolverine", StringComparison.Ordinal) != true)
                .Where(entity => (entity.GetSchema() ?? entity.GetViewSchema() ?? model.GetDefaultSchema()) != Schema)
                .Select(entity => $"{entity.DisplayName()} -> {entity.GetSchema() ?? entity.GetViewSchema() ?? model.GetDefaultSchema() ?? "(padrão do banco)"}")
                .Concat(model.GetSequences()
                    .Where(sequence => (sequence.Schema ?? model.GetDefaultSchema()) != Schema)
                    .Select(sequence => $"sequência {sequence.Name} -> {sequence.Schema ?? "(padrão do banco)"}"))
                .ToList();
            Assert.True(
                outside.Count == 0,
                $"{contextType.Name} mapeia objetos fora do schema \"{Schema}\":\n" + string.Join('\n', outside));
        }
    }

    /// <summary>Instancia o DbContext pelo construtor padrão do EF Core (<c>DbContextOptions&lt;T&gt;</c>) com o provedor PostgreSQL.</summary>
    private static DbContext CreateContext(Type contextType)
    {
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        builder.UseNpgsql("Host=localhost;Database=architecture_tests");
        return (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
    }
}
