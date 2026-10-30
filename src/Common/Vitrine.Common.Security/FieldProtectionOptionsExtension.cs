using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vitrine.Common.Security;

/// <summary>
/// Extensão de opções do EF Core criada por <see cref="FieldProtectionModelExtensions.UseFieldProtection"/>: registra a
/// convenção que instala o conversor de cada propriedade marcada com <c>IsProtected</c>.
/// </summary>
internal sealed class FieldProtectionOptionsExtension(IFieldProtector protector) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public IFieldProtector Protector { get; } = protector;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    // O protetor não entra no provedor interno do EF Core (um só para todos os protetores do processo): a convenção o
    // lê das opções do contexto que monta o modelo, e o modelo é cacheado por protetor (FieldProtectionModelSource).
    // O provedor do banco (UseNpgsql) já registrou o IModelSource padrão quando esta extensão vem depois dele: Replace
    // vale nas duas ordens.
    public void ApplyServices(IServiceCollection services)
    {
        new EntityFrameworkServicesBuilder(services).TryAdd<IConventionSetPlugin, FieldProtectionConventionPlugin>();
        services.Replace(ServiceDescriptor.Singleton<IModelSource, FieldProtectionModelSource>());
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(FieldProtectionOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        // Nada sobre o protetor nos logs de diagnóstico do EF Core.
        public override string LogFragment => "using field protection ";

        // Um provedor interno para qualquer protetor (SPEC-0026): o protetor só diferencia o modelo, não os serviços.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other.Extension is FieldProtectionOptionsExtension;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Vitrine:FieldProtection"] = "1";
    }
}

/// <summary>
/// <see cref="IModelSource"/> que cacheia um modelo por (chave da fábrica registrada, protetor do contexto): compõe com
/// a <see cref="IModelCacheKeyFactory"/> em vigor no provedor interno (a padrão do EF Core ou a trocada por outra
/// biblioteca, como a do Wolverine), sem substituí-la. Cada contexto monta e reutiliza o modelo com o próprio
/// conversor; protetores diferentes nunca compartilham modelo.
/// </summary>
internal sealed class FieldProtectionModelSource(ModelSourceDependencies dependencies)
    : ModelSource(dependencies with { ModelCacheKeyFactory = new ProtectorModelCacheKeyFactory(dependencies.ModelCacheKeyFactory) })
{
    private sealed class ProtectorModelCacheKeyFactory(IModelCacheKeyFactory inner) : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var key = inner.Create(context, designTime);
            var protector = context.GetService<IDbContextOptions>().FindExtension<FieldProtectionOptionsExtension>()?.Protector;
            return protector is null ? key : new ProtectedModelKey(key, protector);
        }
    }

    // Igualdade pela chave interna e pela identidade do protetor (nunca por igualdade de valor do protetor).
    private sealed record ProtectedModelKey(object Inner, IFieldProtector Protector)
    {
        public bool Equals(ProtectedModelKey? other) =>
            other is not null && Inner.Equals(other.Inner) && ReferenceEquals(Protector, other.Protector);

        public override int GetHashCode() =>
            HashCode.Combine(Inner, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Protector));
    }
}

/// <summary>Acrescenta a convenção de campos protegidos ao conjunto de convenções do contexto.</summary>
internal sealed class FieldProtectionConventionPlugin(IDbContextOptions options) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        var extension = options.FindExtension<FieldProtectionOptionsExtension>()
            ?? throw new InvalidOperationException("UseFieldProtection não foi chamado no contexto.");
        conventionSet.ModelFinalizingConventions.Add(new FieldProtectionConvention(extension.Protector));
        return conventionSet;
    }
}

/// <summary>Na finalização do modelo, toda propriedade anotada por <c>IsProtected</c> ganha o conversor do protetor.</summary>
internal sealed class FieldProtectionConvention(IFieldProtector protector) : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var property in modelBuilder.Metadata.GetEntityTypes().SelectMany(entity => entity.GetDeclaredProperties()))
        {
            if (property.FindAnnotation(FieldProtectionModelExtensions.PurposeAnnotation)?.Value is string purpose)
            {
                property.SetValueConverter(FieldProtectionModelExtensions.ConverterFor(protector, purpose));
            }
        }
    }
}
