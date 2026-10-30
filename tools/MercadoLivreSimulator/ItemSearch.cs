namespace Vitrine.MercadoLivreSimulator;

/// <summary>Página da busca por offset (<c>{ results, paging: { total, offset, limit } }</c>).</summary>
public sealed record OffsetPage(IReadOnlyList<string> Results, int Total, int Offset, int Limit);

/// <summary>Página da busca <c>search_type=scan</c> (<c>{ results, scroll_id }</c>; <c>scroll_id</c> nulo no fim).</summary>
public sealed record ScanPage(IReadOnlyList<string> Results, string? ScrollId);

/// <summary>Busca de IDs de anúncios do vendedor (<c>GET /users/{id}/items/search</c>): offset até 1000 e scan.</summary>
public sealed class ItemSearch
{
    /// <summary>Maior <c>offset + limit</c> aceito na busca por offset.</summary>
    public const int MaxOffsetWindow = 1000;

    /// <summary>Página por offset; <c>null</c> quando <c>offset + limit</c> passa de <see cref="MaxOffsetWindow"/> (400).</summary>
    public OffsetPage? ByOffset(IReadOnlyList<string> ids, int offset, int limit) => throw new NotImplementedException();

    /// <summary>Página do scan: sem <paramref name="scrollId"/> começa do início; senão continua de onde parou.</summary>
    public ScanPage Scan(IReadOnlyList<string> ids, string? scrollId, int limit) => throw new NotImplementedException();
}
