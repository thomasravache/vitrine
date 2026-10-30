using System.Runtime.CompilerServices;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Percorre <c>GET /users/{id}/items/search</c> (SPEC-0021): por offset (0, 100, 200…) enquanto <c>paging.total</c> ≤ 1000;
/// acima disso descarta a 1ª página e recomeça com <c>search_type=scan</c>, seguindo o <c>scroll_id</c> até
/// <c>results</c> vazio ou <c>scroll_id</c> nulo. Uma página que não avança (mesmo <c>scroll_id</c> e mesmos
/// <c>results</c> da anterior) ou páginas demais lançam
/// <see cref="MercadoLivreUnavailableException"/> (sem conta: quem chama a completa).
/// </summary>
internal static class ItemSearchPaginator
{
    /// <summary>IDs por página (<c>limit</c>).</summary>
    public const int PageSize = 100;

    /// <summary>Maior <c>paging.total</c> percorrido por offset.</summary>
    public const int OffsetLimit = 1000;

    /// <summary>Páginas de scan além de <c>paging.total / PageSize</c> (o total pode crescer durante a busca).</summary>
    public const int ScanSlackPages = 10;

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

        // Modo scan: a 1ª requisição sem scroll_id; termina com results vazio ou scroll_id nulo. O scroll_id do ML
        // (scroll do Elasticsearch) costuma ser o mesmo em todas as páginas: só uma página que não avança (mesmo
        // scroll_id e os mesmos results da anterior) ou páginas além do total (com folga) indicam laço, e falham.
        var maxPages = (first.Total / PageSize) + ScanSlackPages;
        string? scrollId = null;
        IReadOnlyList<string>? previous = null;
        for (var pages = 1; ; pages++)
        {
            if (pages > maxPages)
            {
                throw new MercadoLivreUnavailableException("a busca por scan passou do total de páginas esperado");
            }

            var page = await fetchPage(new ItemSearchPageRequest(0, Scan: true, ScrollId: scrollId), cancellationToken);
            if (page.Results.Count == 0)
            {
                yield break;
            }

            if (previous is not null
                && string.Equals(page.ScrollId, scrollId, StringComparison.Ordinal)
                && page.Results.SequenceEqual(previous, StringComparer.Ordinal))
            {
                throw new MercadoLivreUnavailableException("a busca por scan repetiu a mesma página");
            }

            previous = page.Results;
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
internal sealed record ItemSearchPageRequest(int Offset, bool Scan, string? ScrollId);

/// <summary>Página devolvida pela busca.</summary>
/// <param name="Results">IDs da página.</param>
/// <param name="Total"><c>paging.total</c> (modo offset; 0 quando ausente).</param>
/// <param name="ScrollId"><c>scroll_id</c> (modo scan).</param>
internal sealed record ItemSearchPage(IReadOnlyList<string> Results, int Total, string? ScrollId);
