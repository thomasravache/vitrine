using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Vitrine.Common.Security;

/// <summary>
/// Conversor EF Core de campos protegidos (SPEC-0008): a propriedade marcada com <see cref="IsProtected"/> é gravada
/// cifrada e lida decifrada pelo <see cref="IFieldProtector"/> ligado ao contexto com <see cref="UseFieldProtection"/>.
/// </summary>
public static class FieldProtectionModelExtensions
{
    /// <summary>Grava a propriedade cifrada com o propósito <paramref name="purpose"/> e a lê decifrada.</summary>
    public static PropertyBuilder<string> IsProtected(this PropertyBuilder<string> builder, string purpose) =>
        throw new NotImplementedException();

    /// <summary>Liga o <see cref="IFieldProtector"/> usado pelas propriedades marcadas com <see cref="IsProtected"/>.</summary>
    public static DbContextOptionsBuilder UseFieldProtection(this DbContextOptionsBuilder options, IFieldProtector protector) =>
        throw new NotImplementedException();
}
