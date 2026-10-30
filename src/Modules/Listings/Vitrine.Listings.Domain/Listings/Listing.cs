using System.Diagnostics.CodeAnalysis;

namespace Vitrine.Listings.Domain.Listings;

/// <summary>
/// Cópia local de um anúncio do Mercado Livre (SPEC-0022), por conta (<c>PK(account_id, item_id)</c>) e marcada com a
/// execução de sincronização que a gravou por último (<c>sync_run_id</c>, base do marcar e varrer).
/// </summary>
public sealed class Listing
{
    public Listing(Guid accountId, string itemId, Guid workspaceId, ListingSnapshot snapshot, Guid? syncRunId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(snapshot);

        AccountId = accountId;
        ItemId = itemId;
        WorkspaceId = workspaceId;
        SyncRunId = syncRunId;
        Copy(snapshot, now);
    }

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

    /// <summary>
    /// Aplica uma leitura do ML se ela não for mais antiga que a cópia (<see cref="ListingUpdatePolicy"/>); falso quando
    /// a leitura foi ignorada.
    /// </summary>
    public bool Refresh(ListingSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ListingUpdatePolicy.ShouldApply(MlLastUpdated, snapshot.LastUpdated))
        {
            return false;
        }

        Copy(snapshot, now);
        return true;
    }

    [MemberNotNull(nameof(Title), nameof(CurrencyId), nameof(Status))]
    private void Copy(ListingSnapshot snapshot, DateTimeOffset now)
    {
        Title = snapshot.Title;
        Price = snapshot.Price;
        CurrencyId = snapshot.CurrencyId;
        AvailableQuantity = snapshot.AvailableQuantity;
        SoldQuantity = snapshot.SoldQuantity;
        Status = snapshot.Status;
        Permalink = snapshot.Permalink;
        ThumbnailUrl = snapshot.ThumbnailUrl;
        MlLastUpdated = snapshot.LastUpdated.ToUniversalTime();
        UpdatedAt = now.ToUniversalTime();
    }
}

/// <summary>Dados de um anúncio lidos do ML, como a cópia local os guarda.</summary>
public sealed record ListingSnapshot(
    string Title,
    decimal Price,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    string? Permalink,
    string? ThumbnailUrl,
    DateTimeOffset LastUpdated);
