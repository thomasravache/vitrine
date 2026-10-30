using System.Runtime.CompilerServices;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Percorre <c>GET /users/{id}/items/search</c> (SPEC-0021): por offset (0, 100, 200…) enquanto <c>paging.total</c> ≤ 1000;
/// acima disso descarta a 1ª página e recomeça com <c>search_type=scan</c>, seguindo o <c>scroll_id</c> até
/// <c>results</c> vazio ou <c>scroll_id</c> nulo.
/// </summary>
public static class ItemSearchPaginator
{
    /// <summary>IDs por página (<c>limit</c>).</summary>
    public const int PageSize = 100;

    /// <summary>Maior <c>paging.total</c> percorrido por offset.</summary>
    public const int OffsetLimit = 1000;

    /// <summary>Páginas de IDs, pedindo cada página a <paramref name="fetchPage"/>.</summary>
    public static async IAsyncEnumerable<IReadOnlyList<string>> PaginateAsync(
        Func<ItemSearchPageRequest, CancellationToken, Task<ItemSearchPage>> fetchPage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);

        // Modo offset: a 1ª página revela o total; acima de OffsetLimit o ML recusa offsets altos, então o scan assume.
        var first = await fetchPage(new ItemSearchPageRequest(0, Scan: false, ScrollId: null), cancellationToken);
        if (first.Total <= OffsetLimit)
        {
            var page = first;
            var offset = 0;
            while (page.Results.Count > 0)
            {
                yield return page.Results;
                offset += PageSize;
                if (offset >= page.Total)
                {
                    yield break;
                }

                page = await fetchPage(new ItemSearchPageRequest(offset, Scan: false, ScrollId: null), cancellationToken);
            }

            yield break;
        }

        // Modo scan: a 1ª requisição sem scroll_id; termina com results vazio ou scroll_id nulo.
        string? scrollId = null;
        while (true)
        {
            var page = await fetchPage(new ItemSearchPageRequest(0, Scan: true, ScrollId: scrollId), cancellationToken);
            if (page.Results.Count == 0)
            {
                yield break;
            }

            yield return page.Results;
            if (string.IsNullOrEmpty(page.ScrollId))
            {
                yield break;
            }

            scrollId = page.ScrollId;
        }
    }
}

/// <summary>Página pedida à busca.</summary>
/// <param name="Offset">Offset da página (modo offset; 0 no modo scan).</param>
/// <param name="Scan">Verdadeiro no modo <c>search_type=scan</c>.</param>
/// <param name="ScrollId"><c>scroll_id</c> da página anterior (modo scan; nulo na primeira).</param>
public sealed record ItemSearchPageRequest(int Offset, bool Scan, string? ScrollId);

/// <summary>Página devolvida pela busca.</summary>
/// <param name="Results">IDs da página.</param>
/// <param name="Total"><c>paging.total</c> (modo offset; 0 quando ausente).</param>
/// <param name="ScrollId"><c>scroll_id</c> (modo scan).</param>
public sealed record ItemSearchPage(IReadOnlyList<string> Results, int Total, string? ScrollId);
