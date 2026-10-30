using System.Globalization;
using System.Text.Json;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Resposta do <c>/items/bulk</c> (<c>[{ id, status_code, body }]</c>) para <see cref="MercadoLivreItemResult"/>
/// (SPEC-0021): 200 → <see cref="MercadoLivreItemResult.Found"/>, 404 → <see cref="MercadoLivreItemResult.NotFound"/>,
/// outro (ou item malformado) → <see cref="MercadoLivreItemResult.Unavailable"/>. Só uma resposta que não é array lança.
/// </summary>
internal static class BulkItemMapper
{
    private const string ThumbnailHost = "mlstatic.com";

    public static IEnumerable<MercadoLivreItemResult> Map(Guid accountId, JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new MercadoLivreUnavailableException(accountId, "resposta inválida do /items/bulk");
        }

        var results = new List<MercadoLivreItemResult>();
        foreach (var entry in response.EnumerateArray())
        {
            // Entrada sem id legível: fica de fora (o ID pedido sem resultado vira Unavailable em quem chama).
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(idElement.GetString()))
            {
                continue;
            }

            var id = idElement.GetString()!;
            results.Add(MapEntry(id, entry));
        }

        return results;
    }

    // Um item malformado (status_code ou body fora do contrato) vira Unavailable só para ele.
    private static MercadoLivreItemResult MapEntry(string id, JsonElement entry)
    {
        try
        {
            return entry.GetProperty("status_code").GetInt32() switch
            {
                200 => new MercadoLivreItemResult.Found(Item(entry.GetProperty("body"))),
                404 => new MercadoLivreItemResult.NotFound(id),
                _ => new MercadoLivreItemResult.Unavailable(id),
            };
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new MercadoLivreItemResult.Unavailable(id);
        }
    }

    private static MercadoLivreItem Item(JsonElement body) =>
        new(
            body.GetProperty("id").GetString()!,
            body.GetProperty("seller_id").GetInt64(),
            body.GetProperty("title").GetString() ?? string.Empty,
            body.GetProperty("price").GetDecimal(),
            body.GetProperty("currency_id").GetString() ?? string.Empty,
            body.GetProperty("available_quantity").GetInt32(),
            body.GetProperty("sold_quantity").GetInt32(),
            body.GetProperty("status").GetString() ?? string.Empty,
            Url(body, "permalink"),
            Thumbnail(body),
            body.TryGetProperty("listing_type_id", out var listingType) && listingType.ValueKind == JsonValueKind.String ? listingType.GetString() : null,
            DateTimeOffset.Parse(body.GetProperty("last_updated").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

    /// <summary>
    /// <c>secure_thumbnail</c>; sem ele, <c>thumbnail</c> com o esquema trocado para https. Só HTTPS em
    /// <c>*.mlstatic.com</c> (a SPA só exibe imagens desse domínio — CSP); qualquer outra URL vira nula.
    /// </summary>
    public static Uri? Thumbnail(JsonElement body)
    {
        var candidate = Text(body, "secure_thumbnail");
        if (candidate is null && Text(body, "thumbnail") is { } thumbnail)
        {
            candidate = thumbnail.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + thumbnail["http://".Length..] : thumbnail;
        }

        return candidate is not null
            && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals(ThumbnailHost, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + ThumbnailHost, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
    }

    private static Uri? Url(JsonElement body, string name) =>
        Text(body, name) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
