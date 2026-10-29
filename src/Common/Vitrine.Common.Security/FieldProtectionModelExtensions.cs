using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vitrine.Common.Security;

/// <summary>
/// Conversor EF Core de campos protegidos (SPEC-0008): a propriedade marcada com <see cref="IsProtected"/> é gravada
/// cifrada e lida decifrada pelo <see cref="IFieldProtector"/> ligado ao contexto com <see cref="UseFieldProtection"/>.
/// </summary>
public static class FieldProtectionModelExtensions
{
    /// <summary>Anotação do modelo com o propósito do campo protegido.</summary>
    public const string PurposeAnnotation = "Vitrine:ProtectedPurpose";

    /// <summary>Grava a propriedade cifrada com o propósito <paramref name="purpose"/> e a lê decifrada.</summary>
    public static PropertyBuilder<string> IsProtected(this PropertyBuilder<string> builder, string purpose)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        // O conversor real (com o IFieldProtector do contexto) é instalado na finalização do modelo, pela convenção
        // registrada em UseFieldProtection: o modelo em si não guarda o protetor.
        return builder.HasAnnotation(PurposeAnnotation, purpose);
    }

    /// <summary>Liga o <see cref="IFieldProtector"/> usado pelas propriedades marcadas com <see cref="IsProtected"/>.</summary>
    public static DbContextOptionsBuilder UseFieldProtection(this DbContextOptionsBuilder options, IFieldProtector protector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);

        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new FieldProtectionOptionsExtension(protector));
        return options;
    }

    /// <summary>Conversor de um campo: grava <see cref="IFieldProtector.Protect"/> e lê <see cref="IFieldProtector.Unprotect"/>.</summary>
    internal static ValueConverter<string, string> ConverterFor(IFieldProtector protector, string purpose) =>
        new(
            plaintext => protector.Protect(plaintext, purpose),
            ciphertext => protector.Unprotect(ciphertext, purpose));
}
