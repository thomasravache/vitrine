using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vitrine.Listings.Application.Browsing;
using Vitrine.Listings.Application.Sync;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.Listings.Infrastructure.Persistence;

/// <summary>Leituras da listagem e do resumo (keyset por <c>ml_last_updated desc, item_id</c>), sempre por workspace.</summary>
internal sealed class ListingsReadModel(ListingsModuleDbContext db) : IListingsReadModel
{
    public async Task<ListingsPage> ListAsync(Guid workspaceId, ListingsFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = db.Listings.AsNoTracking().Where(listing => listing.WorkspaceId == workspaceId);
        if (filter.Status != ListingsFilter.AllStatuses)
        {
            query = query.Where(listing => listing.Status == filter.Status);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(listing => listing.AccountId == accountId);
        }

        if (filter.ItemId is { } itemId)
        {
            query = query.Where(listing => listing.ItemId == itemId);
        }

        if (filter.TitleContains is { } text)
        {
            var pattern = $"%{EscapeLike(text)}%";
            query = query.Where(listing => EF.Functions.ILike(listing.Title, pattern, @"\"));
        }

        if (filter.After is { } after)
        {
            var lastUpdated = after.LastUpdated.ToUniversalTime();
            var lastItemId = after.ItemId;
            // Traduzido para SQL (item_id > @id), comparado na collation "C" da coluna: ordinal.
#pragma warning disable CA1309
            query = query.Where(listing => listing.MlLastUpdated < lastUpdated
                || (listing.MlLastUpdated == lastUpdated && string.Compare(listing.ItemId, lastItemId) > 0));
#pragma warning restore CA1309
        }

        var rows = await query
            .OrderByDescending(listing => listing.MlLastUpdated)
            .ThenBy(listing => listing.ItemId)
            .Take(filter.Limit + 1)
            .Select(listing => new ListingView(
                listing.ItemId,
                listing.AccountId,
                listing.Title,
                listing.Price,
                listing.CurrencyId,
                listing.AvailableQuantity,
                listing.SoldQuantity,
                listing.Status,
                listing.Permalink,
                listing.ThumbnailUrl,
                listing.MlLastUpdated))
            .ToListAsync(cancellationToken);

        if (rows.Count <= filter.Limit)
        {
            return new ListingsPage(rows, null);
        }

        var page = rows.Take(filter.Limit).ToList();
        var last = page[^1];
        return new ListingsPage(page, new ListingsCursor(last.LastUpdated, last.ItemId));
    }

    public async Task<IReadOnlyList<ListingStatusCount>> CountByStatusAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await db.Listings.AsNoTracking()
            .Where(listing => listing.WorkspaceId == workspaceId)
            .GroupBy(listing => listing.Status)
            .Select(group => new ListingStatusCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

    // Trecho literal no ILIKE: \, % e _ do usuário não viram curingas.
    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}

/// <summary>Estados de sincronização das contas do workspace.</summary>
internal sealed class SyncStates(ListingsModuleDbContext db) : ISyncStates
{
    public async Task<IReadOnlyList<SyncStateView>> ListAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await db.SyncStates.AsNoTracking()
            .Where(state => state.WorkspaceId == workspaceId)
            .OrderBy(state => state.AccountId)
            .Select(state => new SyncStateView(
                state.AccountId, state.WorkspaceId, state.Status, state.StartedAt, state.CompletedAt, state.LastManualAt, state.ItemCount, state.LastError))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Destino da sincronização no PostgreSQL (marcar e varrer — SPEC-0022, emenda v1.3): o <c>upsert</c> em lote marca
/// todo anúncio lido com o <c>sync_run_id</c> da execução, mas só troca os dados quando a leitura não é mais antiga que
/// a cópia; a varredura só apaga o que a execução não tocou e que é anterior ao início dela (uma notificação gravada no
/// meio da execução fica).
/// </summary>
internal sealed class ListingsSyncTarget(ListingsModuleDbContext db, TimeProvider clock, DateTimeOffset startedAt) : IListingsSyncTarget
{
    /// <summary>Anúncios gravados por esta instância (métrica <c>vitrine.listings.items_upserted</c>).</summary>
    public int Upserted { get; private set; }

    public async Task UpsertAsync(Guid accountId, Guid syncRunId, IReadOnlyList<MercadoLivreItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > 0)
        {
            Upserted += await ListingsUpsert.ExecuteAsync(db, accountId, syncRunId, items, clock.GetUtcNow(), cancellationToken);
        }
    }

    public Task SweepAsync(Guid accountId, Guid syncRunId, CancellationToken cancellationToken)
    {
        var startedAtUtc = startedAt.ToUniversalTime();
        return db.Listings
            .Where(listing => listing.AccountId == accountId
                && (listing.SyncRunId == null || listing.SyncRunId != syncRunId)
                && listing.UpdatedAt < startedAtUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Gravação de anúncios lidos do ML (sincronização e notificação), num único comando:
/// <list type="bullet">
/// <item>o workspace vem da linha de <c>sync_states</c>, travada em <c>FOR SHARE</c> — conta desconhecida ou apagada não
/// recebe linhas, e a desconexão (que trava a mesma linha em <c>FOR UPDATE</c>) se serializa com a gravação;</item>
/// <item>os dados só são trocados quando a leitura não é mais antiga que a cópia
/// (<c>excluded.ml_last_updated &gt;= ml_last_updated</c>, a regra de <c>ListingUpdatePolicy</c>);</item>
/// <item>com <c>syncRunId</c> (sincronização), toda linha lida fica marcada com a execução; sem ele (notificação), a
/// marca existente é mantida.</item>
/// </list>
/// </summary>
internal static class ListingsUpsert
{
    private static readonly string[] DataColumns =
    [
        "title", "price", "currency_id", "available_quantity", "sold_quantity", "status", "permalink", "thumbnail_url",
        "ml_last_updated", "updated_at",
    ];

    private const string Fresh = "excluded.ml_last_updated >= l.ml_last_updated";

    private const string Insert =
        """
        insert into listings.listings as l (account_id, item_id, workspace_id, title, price, currency_id, available_quantity,
                                            sold_quantity, status, permalink, thumbnail_url, ml_last_updated, sync_run_id, updated_at)
        select @account, i.item_id, s.workspace_id, i.title, i.price, i.currency_id, i.available_quantity, i.sold_quantity,
               i.status, i.permalink, i.thumbnail_url, i.ml_last_updated, @run, @now
        from unnest(@ids, @titles, @prices, @currencies, @available, @sold, @statuses, @permalinks, @thumbnails, @last_updated)
             as i(item_id, title, price, currency_id, available_quantity, sold_quantity, status, permalink, thumbnail_url, ml_last_updated)
        join listings.sync_states s on s.account_id = @account
        for share of s
        on conflict (account_id, item_id) do update set
        """;

    // Sincronização: marca a execução sempre; dados só se a leitura não for mais antiga.
    private static readonly string SyncSql = Insert
        + "    workspace_id = excluded.workspace_id,\n    sync_run_id = excluded.sync_run_id,\n"
        + string.Join(",\n", DataColumns.Select(column => $"    {column} = case when {Fresh} then excluded.{column} else l.{column} end"));

    // Notificação: só atualiza (dados e workspace) quando a leitura não é mais antiga; a marca da execução fica.
    private static readonly string NotificationSql = Insert
        + "    workspace_id = excluded.workspace_id,\n"
        + string.Join(",\n", DataColumns.Select(column => $"    {column} = excluded.{column}"))
        + $"\nwhere {Fresh}";

    /// <summary>Grava os anúncios; devolve quantas linhas foram inseridas ou atualizadas.</summary>
    public static Task<int> ExecuteAsync(
        ListingsModuleDbContext db, Guid accountId, Guid? syncRunId, IReadOnlyList<MercadoLivreItem> items, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        NpgsqlParameter[] parameters =
        [
            new("account", NpgsqlDbType.Uuid) { Value = accountId },
            new("run", NpgsqlDbType.Uuid) { Value = syncRunId is { } run ? run : DBNull.Value },
            new("now", NpgsqlDbType.TimestampTz) { Value = now.ToUniversalTime() },
            Array("ids", NpgsqlDbType.Text, items.Select(item => item.Id).ToArray()),
            Array("titles", NpgsqlDbType.Text, items.Select(item => item.Title).ToArray()),
            Array("prices", NpgsqlDbType.Numeric, items.Select(item => item.Price).ToArray()),
            Array("currencies", NpgsqlDbType.Text, items.Select(item => item.CurrencyId).ToArray()),
            Array("available", NpgsqlDbType.Integer, items.Select(item => item.AvailableQuantity).ToArray()),
            Array("sold", NpgsqlDbType.Integer, items.Select(item => item.SoldQuantity).ToArray()),
            Array("statuses", NpgsqlDbType.Text, items.Select(item => item.Status).ToArray()),
            Array("permalinks", NpgsqlDbType.Text, items.Select(item => item.Permalink?.ToString()).ToArray()),
            Array("thumbnails", NpgsqlDbType.Text, items.Select(item => item.ThumbnailUrl?.ToString()).ToArray()),
            Array("last_updated", NpgsqlDbType.TimestampTz, items.Select(item => item.LastUpdated.ToUniversalTime()).ToArray()),
        ];

        return db.Database.ExecuteSqlRawAsync(syncRunId is null ? NotificationSql : SyncSql, parameters, cancellationToken);
    }

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType elementType, T[] values) =>
        new(name, NpgsqlDbType.Array | elementType) { Value = values };
}
