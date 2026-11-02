using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.UnitTests.MercadoLivre.Listings;

/// <summary>Anúncio e categoria sintéticos para as regras de edição (SPEC-0033): IDs <c>MLB</c> e categoria <c>MLB90001</c>.</summary>
internal static class ListingSamples
{
    public const int MaxTitleLength = 60;

    /// <summary>Anúncio ativo, sem vendas, fora do catálogo e sem user product, com descrição.</summary>
    public static MercadoLivreItemDetails Item(
        int soldQuantity = 0,
        bool catalogListing = false,
        string? userProductId = null,
        string status = "active") =>
        new(
            "MLB5100000001",
            3_987_654_321,
            "MLB90001",
            "Fone De Ouvido Bluetooth Sem Fio Preto",
            soldQuantity,
            status,
            catalogListing,
            userProductId,
            [
                new MercadoLivreItemAttribute("COLOR", "Cor", "52049", "Preto"),
                new MercadoLivreItemAttribute("MODEL", "Modelo", null, "BT-500"),
            ],
            "Fone de ouvido bluetooth sem fio.",
            new DateTimeOffset(2026, 10, 29, 14, 3, 12, TimeSpan.Zero));

    /// <summary>
    /// Categoria com atributo de lista (<c>COLOR</c>), livre (<c>MODEL</c>), <c>number_unit</c> (<c>BATTERY_LIFE</c>, em
    /// <c>h</c> ou <c>min</c>), <c>read_only</c> (<c>ITEM_CONDITION</c>) e <c>hidden</c> (<c>PRODUCT_FEATURES</c>).
    /// </summary>
    public static MercadoLivreCategory Category(int? maxDescriptionLength = 50000) =>
        new("MLB90001", "Fones de Ouvido", MaxTitleLength, maxDescriptionLength, [Color, Model, BatteryLife, ItemCondition, ProductFeatures]);

    public static readonly MercadoLivreCategoryAttribute Color = new(
        "COLOR", "Cor", "list", Required: true, ReadOnly: false, Hidden: false,
        [new("52049", "Preto"), new("52055", "Branco"), new("52100", "Café")], []);

    public static readonly MercadoLivreCategoryAttribute Model = new(
        "MODEL", "Modelo", "string", Required: true, ReadOnly: false, Hidden: false, [], []);

    public static readonly MercadoLivreCategoryAttribute BatteryLife = new(
        "BATTERY_LIFE", "Duração da bateria", "number_unit", Required: false, ReadOnly: false, Hidden: false, [], ["h", "min"]);

    public static readonly MercadoLivreCategoryAttribute ItemCondition = new(
        "ITEM_CONDITION", "Condição do item", "list", Required: false, ReadOnly: true, Hidden: false,
        [new("2230284", "Novo"), new("2230581", "Usado")], []);

    public static readonly MercadoLivreCategoryAttribute ProductFeatures = new(
        "PRODUCT_FEATURES", "Características do produto", "string", Required: false, ReadOnly: false, Hidden: true, [], []);

    public static ListingChanges Title(string title) => new(title, null, null);

    public static ListingChanges Description(string description) => new(null, description, null);

    public static ListingChanges Attributes(params AttributeChange[] attributes) => new(null, null, attributes);
}
