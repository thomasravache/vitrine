using Microsoft.EntityFrameworkCore;
using Vitrine.Listings.Application.Sync;
using Vitrine.Listings.Domain.Sync;
using Vitrine.Listings.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.Listings.Infrastructure.Messaging;

/// <summary>
/// Eventos de conta do módulo MercadoLivre (contratos SPEC-0020@1 e SPEC-0021@1), em filas locais duráveis: conectar
/// importa, desconectar apaga e reautorização pausa.
/// </summary>
public static class AccountEventsHandler
{
    /// <summary>
    /// Cria (ou zera) o estado da conta no workspace do evento e enfileira a importação completa. Uma sincronização em
    /// curso continua <c>running</c> (ela atende a importação).
    /// </summary>
    public static async Task<SyncListings> Handle(
        MercadoLivreAccountConnected message, ListingsModuleDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);

        await db.Database.ExecuteSqlAsync(
            $"""
            insert into listings.sync_states (account_id, workspace_id, status, item_count)
            values ({message.AccountId}, {message.WorkspaceId}, {SyncStatuses.Idle}, 0)
            on conflict (account_id) do update set
                workspace_id = excluded.workspace_id,
                last_error = null,
                status = case when sync_states.status = {SyncStatuses.Running} then sync_states.status else excluded.status end
            """,
            cancellationToken);
        await db.Listings
            .Where(listing => listing.AccountId == message.AccountId && listing.WorkspaceId != message.WorkspaceId)
            .ExecuteUpdateAsync(set => set.SetProperty(listing => listing.WorkspaceId, message.WorkspaceId), cancellationToken);

        return new SyncListings(message.AccountId, SyncTriggers.Connected);
    }

    /// <summary>Apaga o estado e os anúncios da conta (retenção até desconectar).</summary>
    public static async Task Handle(
        MercadoLivreAccountDisconnected message, ListingsModuleDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);

        // Primeiro o estado: uma sincronização em curso deixa de gravar anúncios (o upsert exige a linha de sync_states).
        await db.SyncStates.Where(state => state.AccountId == message.AccountId).ExecuteDeleteAsync(cancellationToken);
        await db.Listings.Where(listing => listing.AccountId == message.AccountId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Pausa a sincronização da conta até ela ser reconectada; os anúncios ficam.</summary>
    public static Task Handle(
        MercadoLivreAccountReauthorizationRequired message, ListingsModuleDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);

        return db.SyncStates
            .Where(state => state.AccountId == message.AccountId)
            .ExecuteUpdateAsync(set => set.SetProperty(state => state.Status, SyncStatuses.Paused), cancellationToken);
    }
}
