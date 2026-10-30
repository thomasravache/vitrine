using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

/// <summary>
/// Fronteiras do módulo MercadoLivre e do contrato <c>Identity.PublicApi</c> (ADR-0001, ADR-0006, ADR-0011 — SPEC-0020:UT-06).
/// </summary>
public sealed class MercadoLivreModuleArchitectureTests
{
    private const string Module = "Vitrine.MercadoLivre";
    private const string Schema = "mercadolivre";

    private static readonly string[] Layers = ["Domain", "Application", "Infrastructure", "Presentation", "IntegrationEvents", "PublicApi"];

    // Camadas que precisam ter tipos (o PublicApi do MercadoLivre pode nascer vazio: a SPEC-0021 o preenche).
    private static readonly string[] LayersWithTypes = ["Domain", "Application", "Infrastructure", "Presentation", "IntegrationEvents"];

    private static readonly string[] IdentityInternalLayers = ["Domain", "Application", "Infrastructure", "Presentation"];

    private static readonly string[] ContractLayers = ["PublicApi", "IntegrationEvents"];

    private static readonly string[] SensitiveNameParts = ["token", "secret", "verifier"];

    // Contrato v1 do gateway (SPEC-0021): interface, resultados, item e exceções públicas.
    private static readonly string[] GatewayContract =
    [
        "IMercadoLivreItems", "MercadoLivreItemResult", "MercadoLivreItem", "MercadoLivreAccountNotFoundException",
        "MercadoLivreReauthorizationRequiredException", "MercadoLivreUnavailableException",
    ];

    private static readonly string[] GatewaySensitiveNameParts = ["token", "secret"];

    private static readonly string[] MercadoLivreInternalLayers = ["Domain", "Application", "Infrastructure", "Presentation"];

    [Fact]
    [Trait("spec", "SPEC-0020:UT-06")]
    public void Assemblies_do_modulo_MercadoLivre_respeitam_camadas_isolamento_e_ausencia_de_ciclos()
    {
        // Dado os assemblies reais da solução, com os seis projetos do módulo MercadoLivre
        var architecture = SolutionArchitecture.Load();
        var assemblyNames = architecture.Assemblies.Select(assembly => assembly.Name).ToList();
        foreach (var layer in Layers)
        {
            Assert.True(
                assemblyNames.Any(name => name.StartsWith($"{Module}.{layer},", StringComparison.Ordinal)
                    || name.Equals($"{Module}.{layer}", StringComparison.Ordinal)),
                $"Assembly {Module}.{layer} não está na arquitetura carregada.");
        }

        foreach (var layer in LayersWithTypes)
        {
            Assert.True(
                architecture.Types.Any(type => type.Namespace.FullName.StartsWith($"{Module}.{layer}", StringComparison.Ordinal)),
                $"Nenhum tipo no namespace {Module}.{layer}: as regras não teriam o que verificar.");
        }

        // Quando as regras de arquitetura (camadas, isolamento, ciclos, Common) rodam
        var failures = RuleEvaluation.Failures(ArchitectureRules.All(architecture), architecture)
            .Where(failure => failure.Contains(Module, StringComparison.Ordinal)
                || failure.Contains("Vitrine.Identity.PublicApi", StringComparison.Ordinal)
                || failure.Contains("Vitrine.Common.Web", StringComparison.Ordinal))
            .ToList();

        // Então o módulo MercadoLivre, o Identity.PublicApi e o Common.Web não violam nenhuma
        Assert.True(failures.Count == 0, "Violações de arquitetura no módulo MercadoLivre/Identity.PublicApi/Common.Web:\n" + string.Join('\n', failures));
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-06")]
    public void Identity_PublicApi_nao_referencia_projetos_internos_do_Identity()
    {
        // Dado o assembly Vitrine.Identity.PublicApi
        var publicApi = SolutionAssembly("Vitrine.Identity.PublicApi");
        Assert.Contains(publicApi.GetExportedTypes(), type => type.Name == "IWorkspaceMembership");

        // Quando as referências dele são lidas
        var internalReferences = publicApi.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => IdentityInternalLayers.Any(layer => name == $"Vitrine.Identity.{layer}"))
            .ToList();

        // Então não há nenhuma camada interna do Identity
        Assert.True(internalReferences.Count == 0, "Identity.PublicApi referencia projetos internos: " + string.Join(", ", internalReferences));
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-06")]
    public void Common_Web_existe_e_nao_referencia_modulos()
    {
        // Dado o building block Vitrine.Common.Web (filtros antiforgery/HTTPS extraídos do Identity)
        var architecture = SolutionArchitecture.Load();
        Assert.True(
            architecture.Assemblies.Any(assembly => assembly.Name.StartsWith("Vitrine.Common.Web,", StringComparison.Ordinal)
                || assembly.Name.Equals("Vitrine.Common.Web", StringComparison.Ordinal)),
            "Assembly Vitrine.Common.Web não está na arquitetura carregada.");
        var commonWeb = SolutionAssembly("Vitrine.Common.Web");

        // Quando as referências dele são lidas; então nenhuma é de módulo (Common não referencia módulos — ADR-0001)
        var moduleReferences = commonWeb.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Vitrine.Identity", StringComparison.Ordinal) || name.StartsWith(Module, StringComparison.Ordinal))
            .ToList();
        Assert.True(moduleReferences.Count == 0, "Vitrine.Common.Web referencia módulos: " + string.Join(", ", moduleReferences));

        // E os filtros não são mais cópias: o Identity não define mais AntiforgeryFilter nem HttpsRequiredFilter
        var identityPresentation = SolutionAssembly("Vitrine.Identity.Presentation");
        var copies = identityPresentation.GetTypes()
            .Where(type => type.Name is "AntiforgeryFilter" or "HttpsRequiredFilter")
            .Select(type => type.FullName)
            .ToList();
        Assert.True(copies.Count == 0, "Filtros ainda definidos no Identity.Presentation (deveriam estar em Vitrine.Common.Web): " + string.Join(", ", copies));
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-06")]
    public void Tipos_publicos_do_contrato_do_MercadoLivre_nao_expoem_token_secret_nem_verifier()
    {
        // Dado os tipos públicos de MercadoLivre.PublicApi e MercadoLivre.IntegrationEvents
        var contractTypes = ContractLayers
            .Select(layer => SolutionAssembly($"{Module}.{layer}"))
            .SelectMany(assembly => assembly.GetExportedTypes())
            .ToList();
        Assert.Contains(contractTypes, type => type.Name == "MercadoLivreAccountConnected");
        Assert.Contains(contractTypes, type => type.Name == "MercadoLivreAccountDisconnected");

        // Quando as propriedades públicas são lidas
        var sensitive = contractTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Where(name => SensitiveNameParts.Any(part => name[(name.LastIndexOf('.') + 1)..].Contains(part, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Então nenhuma tem "token", "secret" ou "verifier" no nome (tokens nunca saem do módulo — ADR-0006)
        Assert.True(sensitive.Count == 0, "Propriedades sensíveis no contrato público do MercadoLivre:\n" + string.Join('\n', sensitive));
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-06")]
    public void DbContexts_do_modulo_MercadoLivre_mapeiam_somente_o_schema_mercadolivre()
    {
        // Dado os DbContexts do módulo MercadoLivre
        var contexts = Layers
            .Select(layer => SolutionAssembly($"{Module}.{layer}"))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(type))
            .ToList();
        Assert.True(contexts.Count > 0, $"O módulo MercadoLivre não tem DbContext (schema \"{Schema}\" próprio — ADR-0011).");

        foreach (var contextType in contexts)
        {
            // Quando o modelo do EF Core é montado (sem conexão com o banco)
            using var context = CreateContext(contextType);
            var model = context.GetService<IDesignTimeModel>().Model;

            // Então toda tabela, visão e sequência fica no schema "mercadolivre" (o outbox do Wolverine é do host)
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

            // E as tabelas do contrato existem
            var tables = model.GetEntityTypes().Select(entity => entity.GetTableName()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            Assert.Contains("accounts", tables);
            Assert.Contains("pending_authorizations", tables);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-04")]
    public void MercadoLivre_PublicApi_tem_o_contrato_do_gateway_e_so_referencia_a_BCL()
    {
        // Dado o assembly Vitrine.MercadoLivre.PublicApi com o contrato v1 da SPEC-0021
        var publicApi = SolutionAssembly($"{Module}.PublicApi");
        var exported = publicApi.GetExportedTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in GatewayContract)
        {
            Assert.True(exported.Contains(name), $"{Module}.PublicApi não exporta {name} (contrato v1 da SPEC-0021).");
        }

        // Quando as referências dele são lidas
        var nonBcl = publicApi.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !IsBcl(name))
            .ToList();

        // Então só há assemblies da BCL (nada do módulo, de Common, de pacotes nem de outros módulos)
        Assert.True(nonBcl.Count == 0, $"{Module}.PublicApi referencia assemblies fora da BCL: " + string.Join(", ", nonBcl));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-04")]
    public void Nenhum_tipo_publico_do_contrato_do_MercadoLivre_tem_membro_com_token_ou_secret()
    {
        // Dado os tipos públicos de MercadoLivre.PublicApi e MercadoLivre.IntegrationEvents (gateway e eventos v1)
        var contractTypes = ContractLayers
            .Select(layer => SolutionAssembly($"{Module}.{layer}"))
            .SelectMany(assembly => assembly.GetExportedTypes())
            .ToList();
        Assert.Contains(contractTypes, type => type.Name == "IMercadoLivreItems");
        Assert.Contains(contractTypes, type => type.Name == "MercadoLivreAccountReauthorizationRequired");

        // Quando os membros públicos (propriedades, campos, métodos e eventos) são lidos
        var sensitive = contractTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(member => member.MemberType is MemberTypes.Property or MemberTypes.Field or MemberTypes.Method or MemberTypes.Event)
                .Select(member => $"{type.FullName}.{member.Name}"))
            .Where(name => GatewaySensitiveNameParts.Any(part => name[(name.LastIndexOf('.') + 1)..].Contains(part, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Então nenhum tem "token" ou "secret" no nome (tokens nunca saem do módulo — ADR-0006)
        Assert.True(sensitive.Count == 0, "Membros sensíveis no contrato público do MercadoLivre:\n" + string.Join('\n', sensitive));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-04")]
    public void Camadas_internas_do_MercadoLivre_nao_sao_referenciadas_por_outros_modulos()
    {
        // Dado os assemblies de src/ fora do módulo MercadoLivre, exceto o host (raiz de composição) e o AppHost
        var architecture = SolutionArchitecture.Load();
        var others = architecture.Assemblies
            .Select(assembly => assembly.Name.Split(',')[0])
            .Where(name => name.StartsWith("Vitrine.", StringComparison.Ordinal)
                && !name.StartsWith($"{Module}.", StringComparison.Ordinal)
                && name is not "Vitrine.Api" and not "Vitrine.AppHost")
            .ToList();
        Assert.NotEmpty(others);

        // Quando as referências deles são lidas
        var violations = others
            .SelectMany(name => SolutionAssembly(name).GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(reference => MercadoLivreInternalLayers.Any(layer => reference == $"{Module}.{layer}"))
                .Select(reference => $"{name} -> {reference}"))
            .ToList();

        // Então nenhum referencia Infrastructure (nem Domain, Application ou Presentation): consumidores (ex.: Listings)
        // usam só MercadoLivre.PublicApi e MercadoLivre.IntegrationEvents
        Assert.True(violations.Count == 0, "Módulos referenciando camadas internas do MercadoLivre:\n" + string.Join('\n', violations));
    }

    private static bool IsBcl(string assemblyName) =>
        assemblyName is "netstandard" or "mscorlib"
        || assemblyName == "System"
        || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);

    /// <summary>
    /// Assembly de um projeto de <c>src/</c> pelo nome, carregado por <see cref="SolutionArchitecture"/> (inclusive os que
    /// nenhum outro projeto referencia, como um PublicApi ainda sem consumidores).
    /// </summary>
    private static Assembly SolutionAssembly(string name)
    {
        _ = SolutionArchitecture.Load();
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == name)
            ?? Assembly.Load(name);
    }

    /// <summary>
    /// Instancia o DbContext pelo construtor do EF Core (<c>DbContextOptions&lt;T&gt;</c>) com o provedor PostgreSQL;
    /// parâmetros extras do construtor (ex.: o <c>IFieldProtector</c> dos tokens) recebem um dublê que não cifra nada.
    /// </summary>
    private static DbContext CreateContext(Type contextType)
    {
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        builder.UseNpgsql("Host=localhost;Database=architecture_tests");
        var constructor = contextType.GetConstructors().OrderBy(candidate => candidate.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(parameter => ArgumentFor(parameter, builder.Options, contextType)).ToArray();
        return (DbContext)constructor.Invoke(arguments);
    }

    private static object ArgumentFor(ParameterInfo parameter, DbContextOptions options, Type contextType)
    {
        if (parameter.ParameterType.IsInstanceOfType(options))
        {
            return options;
        }

        if (parameter.ParameterType == typeof(Vitrine.Common.Security.IFieldProtector))
        {
            return new PassThroughProtector();
        }

        throw new InvalidOperationException($"{contextType.Name}: parâmetro de construtor não suportado ({parameter.ParameterType}).");
    }

    private sealed class PassThroughProtector : Vitrine.Common.Security.IFieldProtector
    {
        public string Protect(string plaintext, string purpose) => plaintext;

        public string Unprotect(string ciphertext, string purpose) => ciphertext;
    }
}
