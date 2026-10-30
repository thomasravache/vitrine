using Microsoft.EntityFrameworkCore;
using Vitrine.Listings.Domain.Listings;
using Vitrine.Listings.Domain.Sync;

namespace Vitrine.Listings.Infrastructure.Persistence;

/// <summary>
/// DbContext do módulo Listings: cópia local dos anúncios e estado de sincronização por conta, no schema
/// <c>listings</c> (ADR-0001, ADR-0011 — SPEC-0022).
/// </summary>
public sealed class ListingsModuleDbContext(DbContextOptions<ListingsModuleDbContext> options) : DbContext(options)
{
    public const string Schema = "listings";

    public DbSet<Listing> Listings => Set<Listing>();

    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        // Busca por trecho do título sem diferenciar maiúsculas (ILIKE) servida por índice trigram (SPEC-0022, IT-11).
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Listing>(listing =>
        {
            listing.ToTable("listings");
            listing.HasKey(l => new { l.AccountId, l.ItemId });
            listing.Property(l => l.AccountId).HasColumnName("account_id");

            // Collation "C": comparação ordinal do código, a mesma do desempate do keyset (emenda v1.2).
            listing.Property(l => l.ItemId).HasColumnName("item_id").HasMaxLength(32).UseCollation("C");
            listing.Property(l => l.WorkspaceId).HasColumnName("workspace_id");
            listing.Property(l => l.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
            listing.Property(l => l.Price).HasColumnName("price").HasPrecision(14, 2);
            listing.Property(l => l.CurrencyId).HasColumnName("currency_id").HasMaxLength(8).IsRequired();
            listing.Property(l => l.AvailableQuantity).HasColumnName("available_quantity");
            listing.Property(l => l.SoldQuantity).HasColumnName("sold_quantity");
            listing.Property(l => l.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            listing.Property(l => l.Permalink).HasColumnName("permalink").HasMaxLength(2048);
            listing.Property(l => l.ThumbnailUrl).HasColumnName("thumbnail_url").HasMaxLength(2048);
            listing.Property(l => l.MlLastUpdated).HasColumnName("ml_last_updated");
            listing.Property(l => l.SyncRunId).HasColumnName("sync_run_id");
            listing.Property(l => l.UpdatedAt).HasColumnName("updated_at");

            listing.HasIndex(l => new { l.WorkspaceId, l.Status, l.MlLastUpdated, l.ItemId })
                .IsDescending(false, false, true, false)
                .HasDatabaseName("ix_listings_workspace_status_last_updated");
            listing.HasIndex(l => new { l.WorkspaceId, l.MlLastUpdated, l.ItemId })
                .IsDescending(false, true, false)
                .HasDatabaseName("ix_listings_workspace_last_updated");
            listing.HasIndex(l => l.Title)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops")
                .HasDatabaseName("ix_listings_title_trgm");
        });

        modelBuilder.Entity<SyncState>(state =>
        {
            state.ToTable("sync_states");
            state.HasKey(s => s.AccountId);
            state.Property(s => s.AccountId).HasColumnName("account_id").ValueGeneratedNever();
            state.Property(s => s.WorkspaceId).HasColumnName("workspace_id");
            state.Property(s => s.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            state.Property(s => s.StartedAt).HasColumnName("started_at");
            state.Property(s => s.CompletedAt).HasColumnName("completed_at");
            state.Property(s => s.LastManualAt).HasColumnName("last_manual_at");
            state.Property(s => s.ItemCount).HasColumnName("item_count");
            state.Property(s => s.LastError).HasColumnName("last_error").HasMaxLength(32);
            state.HasIndex(s => s.WorkspaceId);
        });
    }
}
