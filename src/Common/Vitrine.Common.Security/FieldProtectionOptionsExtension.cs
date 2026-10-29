using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkServicesBuilder(services)
            .TryAdd<IConventionSetPlugin, FieldProtectionConventionPlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(FieldProtectionOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        // Nada sobre o protetor nos logs de diagnóstico do EF Core.
        public override string LogFragment => "using field protection ";

        // Cada protetor tem seu provedor de serviços interno (a convenção o captura).
        public override int GetServiceProviderHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(extension.Protector);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other.Extension is FieldProtectionOptionsExtension otherExtension
            && ReferenceEquals(otherExtension.Protector, extension.Protector);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Vitrine:FieldProtection"] = "1";
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
