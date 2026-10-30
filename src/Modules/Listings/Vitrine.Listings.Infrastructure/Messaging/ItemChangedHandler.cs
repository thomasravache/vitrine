using Microsoft.EntityFrameworkCore;
using Vitrine.Listings.Domain.Listings;
using Vitrine.Listings.Domain.Sync;
using Vitrine.Listings.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;
using Vitrine.MercadoLivre.PublicApi;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Vitrine.Listings.Infrastructure.Messaging;

/// <summary>
/// Notificação de anúncio alterado (contrato SPEC-0023@1): relê o anúncio no ML e atualiza a cópia (se a leitura não for
/// mais antiga que ela), apaga (404) ou ignora (conta desconhecida ou em reautorização).
/// </summary>
public static class ItemChangedHandler
{
    /// <summary>ML indisponível: retentativa durável com espera de 1, 5 e 15 min (SPEC-0022 §5).</summary>
    public static void Configure(HandlerChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        chain.OnException<MercadoLivreUnavailableException>().ScheduleRetry(SyncListingsHandler.RetryDelays);
    }

    public static async Task Handle(
        MercadoLivreItemChanged message,
        ListingsModuleDbContext db,
        IMercadoLivreItems items,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(clock);

        var state = await db.SyncStates.AsNoTracking()
            .Where(candidate => candidate.AccountId == message.AccountId)
            .Select(candidate => new { candidate.WorkspaceId, candidate.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (state is null || state.Status == SyncStatuses.Paused)
        {
            return;
        }

        MercadoLivreItemResult? result;
        try
        {
            result = (await items.GetItemsAsync(message.AccountId, [message.ItemId], cancellationToken))
                .FirstOrDefault(candidate => candidate.ItemId == message.ItemId);
        }
        catch (Exception exception) when (exception is MercadoLivreReauthorizationRequiredException or MercadoLivreAccountNotFoundException)
        {
            // A conta saiu do ar no ML (o evento correspondente pausa ou apaga a cópia).
            return;
        }

        switch (result)
        {
            case MercadoLivreItemResult.Found { Item: var item }:
                var snapshot = SyncListingsHandler.Snapshot(item);
                var now = clock.GetUtcNow();
                var listing = await db.Listings.FindAsync([message.AccountId, message.ItemId], cancellationToken);
                if (listing is null)
                {
                    db.Listings.Add(new Listing(message.AccountId, message.ItemId, state.WorkspaceId, snapshot, syncRunId: null, now));
                }
                else if (!listing.Refresh(snapshot, now))
                {
                    return;
                }

                await db.SaveChangesAsync(cancellationToken);
                break;

            case MercadoLivreItemResult.NotFound:
                await db.Listings
                    .Where(candidate => candidate.AccountId == message.AccountId && candidate.ItemId == message.ItemId)
                    .ExecuteDeleteAsync(cancellationToken);
                break;
        }
    }
}
