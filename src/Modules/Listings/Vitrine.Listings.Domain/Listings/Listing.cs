namespace Vitrine.Listings.Domain.Listings;

/// <summary>
/// Cópia local de um anúncio do Mercado Livre (SPEC-0022), por conta (<c>PK(account_id, item_id)</c>) e marcada com a
/// execução de sincronização que a gravou por último (<c>sync_run_id</c>, base do marcar e varrer). A gravação é feita em
/// lote no banco, aplicando <see cref="ListingUpdatePolicy"/>; esta classe é o modelo de leitura e de mapeamento.
/// </summary>
public sealed class Listing
{
    // Materialização pelo EF Core.
    private Listing()
    {
        ItemId = string.Empty;
        Title = string.Empty;
        CurrencyId = string.Empty;
        Status = string.Empty;
    }

    public Guid AccountId { get; private set; }

    public string ItemId { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public string Title { get; private set; }

    public decimal Price { get; private set; }

    public string CurrencyId { get; private set; }

    public int AvailableQuantity { get; private set; }

    public int SoldQuantity { get; private set; }

    /// <summary>Situação no ML (<c>active</c> | <c>paused</c> | <c>closed</c> | <c>under_review</c> ...).</summary>
    public string Status { get; private set; }

    public string? Permalink { get; private set; }

    public string? ThumbnailUrl { get; private set; }

    /// <summary>Última alteração no ML (UTC).</summary>
    public DateTimeOffset MlLastUpdated { get; private set; }

    public Guid? SyncRunId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
