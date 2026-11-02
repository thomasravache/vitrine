using System.Text.Json.Nodes;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Fixtures sintéticas da SPEC-0033 em <c>tests/Fixtures/MercadoLivre</c> (copiadas para <c>MercadoLivre/Fixtures</c> na
/// saída): anúncio, descrição, categoria, atributos da categoria, corpos de escrita e erros.
/// </summary>
internal static class ListingFixtures
{
    public const string CategoryId = "MLB90001";

    /// <summary>Fixture inteira (<c>_source</c> + <c>response</c>/<c>request</c>).</summary>
    public static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MercadoLivre", "Fixtures", name)))!;

    /// <summary><c>response</c> de uma fixture de leitura.</summary>
    public static JsonNode Response(string name) => Load(name)["response"]!.DeepClone();

    /// <summary><c>request</c> de uma fixture de escrita.</summary>
    public static JsonNode Request(string name) => Load(name)["request"]!.DeepClone();

    /// <summary>
    /// <c>item-details.json</c> com <paramref name="itemId"/> e <paramref name="sellerId"/>; <paramref name="edit"/> ajusta
    /// outros campos (ex.: <c>sold_quantity</c>, <c>status</c>).
    /// </summary>
    public static JsonObject Item(string itemId, long sellerId, Action<JsonObject>? edit = null)
    {
        var item = Response("item-details.json").AsObject();
        item["id"] = itemId;
        item["seller_id"] = sellerId;
        edit?.Invoke(item);
        return item;
    }

    public static JsonNode Description() => Response("item-description.json");

    public static JsonNode Category() => Response("category.json");

    public static JsonNode CategoryAttributes() => Response("category-attributes.json");

    /// <summary>Corpo esperado do <c>PUT /items/{id}</c> com título e dois atributos (lista e livre).</summary>
    public static JsonNode ItemUpdateRequest() => Request("item-update-request.json");

    /// <summary>Corpo esperado do <c>PUT/POST /items/{id}/description</c>.</summary>
    public static JsonNode DescriptionRequest() => Request("item-description-request.json");

    /// <summary>Mudanças que produzem exatamente os corpos de <c>item-update-request.json</c> e <c>item-description-request.json</c>.</summary>
    public static ListingChanges ChangesFromRequestFixtures()
    {
        var update = ItemUpdateRequest();
        var attributes = update["attributes"]!.AsArray()
            .Select(attribute => new AttributeChange(
                attribute!["id"]!.GetValue<string>(),
                attribute["value_id"]?.GetValue<string>(),
                attribute["value_name"]?.GetValue<string>()))
            .ToList();
        return new ListingChanges(
            update["title"]!.GetValue<string>(),
            DescriptionRequest()["plain_text"]!.GetValue<string>(),
            attributes);
    }
}
