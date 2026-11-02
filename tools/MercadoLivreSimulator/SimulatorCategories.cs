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
/// <remarks>
/// Os <c>value_id</c> de <c>COLOR</c> e <c>ITEM_CONDITION</c> seguem as fixtures do ML (Preto <c>52049</c>, Branco
/// <c>52055</c>, Novo <c>2230284</c> — Emenda v1.1); os demais são sintéticos.
/// </remarks>
internal static class SimulatorCategories
{
    /// <summary>Limite de <c>settings.max_title_length</c> das categorias sintéticas.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>Limite de <c>settings.max_description_length</c> das categorias sintéticas.</summary>
    public const int MaxDescriptionLength = 50_000;

    /// <summary>Todas as categorias sintéticas.</summary>
    public static IReadOnlyList<SimulatorCategory> All { get; } =
    [
        Category(
            "MLB90001",
            "Eletrônicos e acessórios (simulado)",
            List("IS_WIRELESS", "É sem fio", "boolean", ("242085", "Sim"), ("242084", "Não"))),
        Category(
            "MLB90002",
            "Casa e cozinha (simulado)",
            NumberUnit("CAPACITY", "Capacidade", ("mL", "mL"), ("L", "L")),
            List("MATERIAL", "Material", "list", ("90002001", "Aço inoxidável"), ("90002002", "Cerâmica"), ("90002003", "Plástico"))),
        Category(
            "MLB90003",
            "Bolsas e mochilas (simulado)",
            NumberUnit("LAPTOP_SIZE", "Tamanho máximo do notebook", ("pol", "pol")),
            List("MATERIAL", "Material", "list", ("90003001", "Poliéster"), ("90003002", "Nylon"), ("90003003", "Couro sintético"))),
    ];

    /// <summary>Categoria pelo id; <c>null</c> quando não existe.</summary>
    public static SimulatorCategory? Find(string categoryId) =>
        All.FirstOrDefault(category => string.Equals(category.Id, categoryId, StringComparison.Ordinal));

    /// <summary>Categoria com os atributos comuns (BRAND, MODEL, COLOR, ITEM_CONDITION, PRODUCT_DATA_SOURCE) e os próprios.</summary>
    private static SimulatorCategory Category(string id, string name, params CategoryAttribute[] specific) =>
        new(
            id,
            name,
            MaxTitleLength,
            MaxDescriptionLength,
            [
                new CategoryAttribute("BRAND", "Marca", "string", Required: true, false, false, false, [], []),
                new CategoryAttribute("MODEL", "Modelo", "string", false, false, false, false, [], []),
                new CategoryAttribute(
                    "COLOR",
                    "Cor",
                    "list",
                    Required: true,
                    false,
                    false,
                    false,
                    [new("52049", "Preto"), new("52055", "Branco"), new("52028", "Azul"), new("51993", "Vermelho")],
                    []),
                new CategoryAttribute("ITEM_CONDITION", "Condição do item", "list", false, false, ReadOnly: true, false, [new("2230284", "Novo")], []),
                new CategoryAttribute("PRODUCT_DATA_SOURCE", "Origem dos dados do produto", "string", false, false, false, Hidden: true, [], []),
                .. specific,
            ]);

    private static CategoryAttribute List(string id, string name, string valueType, params (string Id, string Name)[] values) =>
        new(id, name, valueType, false, false, false, false, [.. values.Select(value => new CategoryValue(value.Id, value.Name))], []);

    private static CategoryAttribute NumberUnit(string id, string name, params (string Id, string Name)[] units) =>
        new(id, name, "number_unit", false, false, false, false, [], [.. units.Select(unit => new CategoryUnit(unit.Id, unit.Name))]);
}
