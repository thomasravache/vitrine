namespace Vitrine.MercadoLivre.PublicApi;

/// <summary>
/// Leitura de anúncios do Mercado Livre em nome de uma conta conectada (contrato v1 da SPEC-0021, ADR-0001): a única
/// porta para outros módulos lerem a API do ML. Os tokens nunca saem do módulo (ADR-0006). Os dois métodos lançam
/// <see cref="MercadoLivreAccountNotFoundException"/>, <see cref="MercadoLivreReauthorizationRequiredException"/> e
/// <see cref="MercadoLivreUnavailableException"/>.
/// </summary>
public interface IMercadoLivreItems
{
    /// <summary>
    /// IDs (ex.: <c>MLB1234567890</c>) dos anúncios da conta com o <paramref name="status"/> pedido
    /// (<c>active</c> | <c>paused</c> | <c>closed</c>), em páginas de até 100. Ordem não garantida; pode repetir IDs entre
    /// páginas (o consumidor deduplica).
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<string>> SearchItemIdsAsync(Guid accountId, string status, CancellationToken cancellationToken);

    /// <summary>
    /// Lê os anúncios em lote (<c>/items/bulk</c>), quebrando em lotes de <c>MercadoLivre:BulkBatchSize</c>. Um resultado
    /// por ID pedido, na mesma ordem.
    /// </summary>
    Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(
        Guid accountId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken);
}

/// <summary>Resultado da leitura de um anúncio no lote, pelo <c>status_code</c> do item.</summary>
/// <param name="ItemId">ID pedido.</param>
public abstract record MercadoLivreItemResult(string ItemId)
{
    /// <summary>Anúncio lido (<c>status_code</c> 200).</summary>
    public sealed record Found(MercadoLivreItem Item) : MercadoLivreItemResult(Item?.Id ?? string.Empty);

    /// <summary>Anúncio inexistente (<c>status_code</c> 404).</summary>
    public sealed record NotFound(string ItemId) : MercadoLivreItemResult(ItemId);

    /// <summary>Outro <c>status_code</c> do item (ex.: 403); não lança.</summary>
    public sealed record Unavailable(string ItemId) : MercadoLivreItemResult(ItemId);
}

/// <summary>Anúncio do Mercado Livre com os campos usados pelo Vitrine (contrato v1 da SPEC-0021).</summary>
/// <param name="Id">ID do anúncio (ex.: <c>MLB1234567890</c>).</param>
/// <param name="SellerId">ID do vendedor no ML (int64).</param>
/// <param name="Title">Título.</param>
/// <param name="Price">Preço.</param>
/// <param name="CurrencyId">Moeda (ex.: <c>BRL</c>).</param>
/// <param name="AvailableQuantity">Quantidade disponível.</param>
/// <param name="SoldQuantity">Quantidade vendida.</param>
/// <param name="Status">Situação no ML (<c>active</c> | <c>paused</c> | <c>closed</c> | <c>under_review</c> | <c>inactive</c> ...).</param>
/// <param name="Permalink">Página pública do anúncio.</param>
/// <param name="ThumbnailUrl">Miniatura HTTPS em <c>*.mlstatic.com</c>; nula fora disso.</param>
/// <param name="ListingTypeId">Tipo de anúncio (ex.: <c>gold_special</c>).</param>
/// <param name="LastUpdated">Última alteração no ML.</param>
public sealed record MercadoLivreItem(
    string Id,
    long SellerId,
    string Title,
    decimal Price,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    Uri? Permalink,
    Uri? ThumbnailUrl,
    string? ListingTypeId,
    DateTimeOffset LastUpdated);
