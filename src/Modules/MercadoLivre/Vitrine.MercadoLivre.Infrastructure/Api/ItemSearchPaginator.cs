namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Percorre <c>GET /users/{id}/items/search</c> (SPEC-0021): por offset (0, 100, 200…) enquanto <c>paging.total</c> ≤ 1000;
/// acima disso descarta a 1ª página e recomeça com <c>search_type=scan</c>, seguindo o <c>scroll_id</c> até
/// <c>results</c> vazio ou <c>scroll_id</c> nulo. Scaffold do contrato.
/// </summary>
public static class ItemSearchPaginator
{
    /// <summary>Páginas de IDs, pedindo cada página a <paramref name="fetchPage"/>.</summary>
    public static IAsyncEnumerable<IReadOnlyList<string>> PaginateAsync(
        Func<ItemSearchPageRequest, CancellationToken, Task<ItemSearchPage>> fetchPage, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0021: implementado na fase Green.");
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
