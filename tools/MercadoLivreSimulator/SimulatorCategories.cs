namespace Vitrine.MercadoLivreSimulator;

/// <summary>Valor permitido de um atributo de categoria (<c>values[]</c>).</summary>
internal sealed record CategoryValue(string Id, string Name);

/// <summary>Unidade permitida de um atributo <c>number_unit</c> (<c>allowed_units[]</c>).</summary>
internal sealed record CategoryUnit(string Id, string Name);

/// <summary>Atributo de uma categoria (<c>GET /categories/{id}/attributes</c>), com as tags usadas pelo Vitrine.</summary>
internal sealed record CategoryAttribute(
    string Id,
    string Name,
    string ValueType,
    bool Required,
    bool CatalogRequired,
    bool ReadOnly,
    bool Hidden,
    IReadOnlyList<CategoryValue> Values,
    IReadOnlyList<CategoryUnit> AllowedUnits);

/// <summary>Categoria sintética (<c>GET /categories/{id}</c>) com os limites de <c>settings</c> e os atributos.</summary>
internal sealed record SimulatorCategory(
    string Id,
    string Name,
    int MaxTitleLength,
    int MaxDescriptionLength,
    IReadOnlyList<CategoryAttribute> Attributes);

/// <summary>Catálogo estático das 3 categorias sintéticas do simulador (SPEC-0034, seção 6).</summary>
internal static class SimulatorCategories
{
    /// <summary>Todas as categorias sintéticas.</summary>
    public static IReadOnlyList<SimulatorCategory> All => throw new NotImplementedException("SPEC-0034: scaffold.");

    /// <summary>Categoria pelo id; <c>null</c> quando não existe.</summary>
    public static SimulatorCategory? Find(string categoryId) => throw new NotImplementedException("SPEC-0034: scaffold.");
}
