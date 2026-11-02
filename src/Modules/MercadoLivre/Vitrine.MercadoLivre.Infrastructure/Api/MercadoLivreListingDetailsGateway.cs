using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <see cref="IMercadoLivreListingDetails"/> pelo cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0033):
/// <c>GET /items/{id}</c> + <c>/description</c>, com a guarda de vendedor da SPEC-0029 (anúncio de outro vendedor é
/// tratado como inexistente), e <c>GET /categories/{id}</c> + <c>/attributes</c> com cache em memória de 24 h por
/// categoria, só para respostas 200. A validade do cache segue o <see cref="TimeProvider"/> do contêiner.
/// </summary>
internal sealed class MercadoLivreListingDetailsGateway(
    MercadoLivreModuleDbContext db,
    MercadoLivreApiCaller api,
    IMemoryCache cache,
    TimeProvider clock) : IMercadoLivreListingDetails
{
    /// <summary>Campos lidos do <c>GET /items/{id}</c> (contrato da SPEC-0033, seção 6).</summary>
    public const string ItemAttributes =
        "id,seller_id,category_id,title,sold_quantity,status,catalog_listing,user_product_id,permalink,last_updated,attributes";

    /// <summary>Validade da categoria no cache.</summary>
    public static readonly TimeSpan CategoryCacheDuration = TimeSpan.FromHours(24);

    // Modelos dos caminhos nos spans (sem IDs).
    internal const string ItemEndpoint = "/items/{id}";
    internal const string DescriptionEndpoint = "/items/{id}/description";
    private const string CategoryEndpoint = "/categories/{id}";
    private const string CategoryAttributesEndpoint = "/categories/{id}/attributes";

    public async Task<MercadoLivreItemDetails?> GetItemDetailsAsync(Guid accountId, string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var mlUserId = await MlUserIdAsync(accountId, cancellationToken);
        var path = ItemPath(itemId);

        MercadoLivreItemDetails item;
        using (var document = await api.GetJsonOrNullAsync(accountId, ItemEndpoint, $"{path}?attributes={ItemAttributes}", cancellationToken))
        {
            if (document is null)
            {
                return null;
            }

            item = Map(accountId, document.RootElement, MapItem);
        }

        // Anúncio de outro vendedor: tratado como inexistente (guarda da SPEC-0029), sem ler a descrição.
        if (item.SellerId != mlUserId)
        {
            return null;
        }

        using var description = await api.GetJsonOrNullAsync(accountId, DescriptionEndpoint, $"{path}/description", cancellationToken);
        return description is null ? item : item with { Description = Map(accountId, description.RootElement, PlainText) };
    }

    public async Task<MercadoLivreCategory?> GetCategoryAsync(Guid accountId, string categoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        await MlUserIdAsync(accountId, cancellationToken);

        var key = $"ml:category:{categoryId}";
        if (cache.TryGetValue(key, out CachedCategory? cached) && cached is not null && clock.GetUtcNow() < cached.ExpiresAt)
        {
            return cached.Category;
        }

        var path = $"categories/{Uri.EscapeDataString(categoryId)}";
        MercadoLivreCategory category;
        using (var document = await api.GetJsonOrNullAsync(accountId, CategoryEndpoint, path, cancellationToken))
        {
            if (document is null)
            {
                return null;
            }

            category = Map(accountId, document.RootElement, MapCategory);
        }

        using (var attributes = await api.GetJsonOrNullAsync(accountId, CategoryAttributesEndpoint, $"{path}/attributes", cancellationToken))
        {
            if (attributes is null)
            {
                return null;
            }

            category = category with { Attributes = Map(accountId, attributes.RootElement, MapCategoryAttributes) };
        }

        // Só respostas 200 entram no cache. A expiração do IMemoryCache (relógio real) só libera memória; a validade é
        // conferida pelo TimeProvider acima.
        cache.Set(key, new CachedCategory(category, clock.GetUtcNow() + CategoryCacheDuration), CategoryCacheDuration);
        return category;
    }

    /// <summary>Caminho relativo do anúncio, com o id escapado.</summary>
    internal static string ItemPath(string itemId) => $"items/{Uri.EscapeDataString(itemId)}";

    private static T Map<T>(Guid accountId, JsonElement element, Func<JsonElement, T> map)
    {
        try
        {
            return map(element);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException
            or ArgumentException)
        {
            throw new MercadoLivreUnavailableException(accountId, "resposta inválida", exception);
        }
    }

    private static MercadoLivreItemDetails MapItem(JsonElement body) =>
        new(
            body.GetProperty("id").GetString() ?? throw new FormatException("id ausente"),
            body.GetProperty("seller_id").GetInt64(),
            body.GetProperty("category_id").GetString() ?? throw new FormatException("category_id ausente"),
            OptionalString(body, "title") ?? string.Empty,
            body.TryGetProperty("sold_quantity", out var sold) && sold.ValueKind == JsonValueKind.Number ? sold.GetInt32() : 0,
            OptionalString(body, "status") ?? string.Empty,
            body.TryGetProperty("catalog_listing", out var catalog) && catalog.ValueKind == JsonValueKind.True,
            OptionalString(body, "user_product_id"),
            body.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array
                ? [.. attributes.EnumerateArray().Where(attribute => OptionalString(attribute, "id") is not null).Select(MapItemAttribute)]
                : [],
            Description: null,
            DateTimeOffset.Parse(
                body.GetProperty("last_updated").GetString() ?? throw new FormatException("last_updated ausente"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal));

    private static MercadoLivreItemAttribute MapItemAttribute(JsonElement attribute) =>
        new(
            OptionalString(attribute, "id")!,
            OptionalString(attribute, "name") ?? string.Empty,
            OptionalString(attribute, "value_id"),
            OptionalString(attribute, "value_name"));

    // 200 do /description: o anúncio tem descrição (texto puro; vazio se o ML não mandar plain_text).
    private static string PlainText(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? OptionalString(body, "plain_text") ?? string.Empty : throw new FormatException("descrição inválida");

    private static MercadoLivreCategory MapCategory(JsonElement body)
    {
        var settings = body.GetProperty("settings");
        return new(
            body.GetProperty("id").GetString() ?? throw new FormatException("id ausente"),
            OptionalString(body, "name") ?? string.Empty,
            settings.GetProperty("max_title_length").GetInt32(),
            settings.TryGetProperty("max_description_length", out var description) && description.ValueKind == JsonValueKind.Number
                ? description.GetInt32()
                : null,
            []);
    }

    private static List<MercadoLivreCategoryAttribute> MapCategoryAttributes(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("atributos da categoria não são um array");
        }

        return [.. body.EnumerateArray().Where(attribute => OptionalString(attribute, "id") is not null).Select(MapCategoryAttribute)];
    }

    private static MercadoLivreCategoryAttribute MapCategoryAttribute(JsonElement attribute)
    {
        var tags = attribute.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object ? tagsElement : default;
        bool Tag(string name) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var tag) && tag.ValueKind == JsonValueKind.True;

        return new(
            OptionalString(attribute, "id")!,
            OptionalString(attribute, "name") ?? string.Empty,
            OptionalString(attribute, "value_type") ?? string.Empty,
            Tag("required") || Tag("catalog_required"),
            Tag("read_only"),
            Tag("hidden"),
            [.. Array(attribute, "values")
                .Where(value => OptionalString(value, "id") is not null)
                .Select(value => new MercadoLivreAttributeValue(OptionalString(value, "id")!, OptionalString(value, "name") ?? string.Empty))],
            [.. Array(attribute, "allowed_units").Select(unit => OptionalString(unit, "id")).OfType<string>()]);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object)
            : [];

    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Id do vendedor da conta; lança se ela não existe ou precisa ser reconectada (sem chamar o ML).</summary>
    private async Task<long> MlUserIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        return MercadoLivreAccessTokenProvider.EnsureUsable(accountId, account).MlUserId;
    }

    private sealed record CachedCategory(MercadoLivreCategory Category, DateTimeOffset ExpiresAt);
}
