using System.Globalization;
using System.Text;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Página da busca por offset (<c>{ results, paging: { total, offset, limit } }</c>).</summary>
public sealed record OffsetPage(IReadOnlyList<string> Results, int Total, int Offset, int Limit);

/// <summary>Página da busca <c>search_type=scan</c> (<c>{ results, scroll_id }</c>; <c>scroll_id</c> nulo no fim).</summary>
public sealed record ScanPage(IReadOnlyList<string> Results, string? ScrollId);

/// <summary>Busca de IDs de anúncios do vendedor (<c>GET /users/{id}/items/search</c>): offset até 1000 e scan.</summary>
/// <remarks>
/// O <c>scroll_id</c> é opaco para o cliente e carrega a posição do próprio scan (cada scan tem o seu cursor). O scan
/// termina com <c>scroll_id</c> nulo na última página com resultados (ou numa página vazia, se a lista estiver vazia).
/// </remarks>
public sealed class ItemSearch
{
    /// <summary>Maior <c>offset + limit</c> aceito na busca por offset.</summary>
    public const int MaxOffsetWindow = 1000;

    private const string ScrollPrefix = "vitrine-sim-scan:";

    /// <summary>Página por offset; <c>null</c> quando <c>offset + limit</c> passa de <see cref="MaxOffsetWindow"/> (400).</summary>
    public OffsetPage? ByOffset(IReadOnlyList<string> ids, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (offset < 0 || limit < 1 || offset + limit > MaxOffsetWindow)
        {
            return null;
        }

        return new OffsetPage([.. ids.Skip(offset).Take(limit)], ids.Count, offset, limit);
    }

    /// <summary>Página do scan: sem <paramref name="scrollId"/> começa do início; senão continua de onde parou.</summary>
    public ScanPage Scan(IReadOnlyList<string> ids, string? scrollId, int limit)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var start = scrollId is null ? 0 : Position(scrollId);
        if (start is null || start.Value >= ids.Count)
        {
            return new ScanPage([], null);
        }

        var results = ids.Skip(start.Value).Take(limit).ToList();
        var next = start.Value + results.Count;
        return new ScanPage(results, next >= ids.Count ? null : Encode(next));
    }

    private static string Encode(int position) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(ScrollPrefix + position.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Posição carregada pelo <c>scroll_id</c>; <c>null</c> quando ele não é deste simulador.</summary>
    private static int? Position(string scrollId)
    {
        try
        {
            var base64 = scrollId.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return text.StartsWith(ScrollPrefix, StringComparison.Ordinal)
                && int.TryParse(text.AsSpan(ScrollPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var position)
                    ? position
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
