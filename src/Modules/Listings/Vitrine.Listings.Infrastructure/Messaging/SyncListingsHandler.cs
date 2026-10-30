using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vitrine.Listings.Application.Sync;
using Vitrine.Listings.Domain.Sync;
using Vitrine.Listings.Infrastructure.Diagnostics;
using Vitrine.Listings.Infrastructure.Persistence;
using Vitrine.MercadoLivre.PublicApi;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Vitrine.Listings.Infrastructure.Messaging;

/// <summary>
/// Sincronização de uma conta (comando durável <see cref="SyncListings"/> — SPEC-0022): marca <c>running</c> (se já
/// estiver, descarta), roda o marcar e varrer e registra o resultado. <c>Unavailable</c> deixa <c>failed</c> e a
/// mensagem é retentada pelo Wolverine (1, 5 e 15 min); <c>ReauthorizationRequired</c> deixa <c>paused</c>, sem
/// retentar. Os anúncios gravados ficam em toda falha (a varredura só roda no fim).
/// </summary>
public static partial class SyncListingsHandler
{
    /// <summary>Esperas da retentativa durável quando o ML está indisponível.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public static void Configure(HandlerChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        chain.OnException<MercadoLivreUnavailableException>().ScheduleRetry(RetryDelays);
    }

    public static async Task Handle(
        SyncListings command,
        ListingsModuleDbContext db,
        IMercadoLivreItems items,
        TimeProvider clock,
        ListingsMetrics metrics,
        ILogger<ListingsSynchronizer> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(metrics);

        var accountId = command.AccountId;
        var startedAt = clock.GetUtcNow();
        var staleBefore = startedAt - StaleRunningSync.StaleRunningAfter;

        // Uma sincronização por conta por vez: só quem muda a linha para running segue.
        var claimed = await db.SyncStates
            .Where(state => state.AccountId == accountId
                && state.Status != SyncStatuses.Paused
                && (state.Status != SyncStatuses.Running || state.StartedAt == null || state.StartedAt < staleBefore))
            .ExecuteUpdateAsync(
                set => set.SetProperty(state => state.Status, SyncStatuses.Running).SetProperty(state => state.StartedAt, startedAt),
                cancellationToken);
        if (claimed == 0)
        {
            Skipped(logger, accountId, command.Trigger);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var target = new ListingsSyncTarget(db, clock, startedAt);
        try
        {
            await new ListingsSynchronizer(items, target).SynchronizeAsync(accountId, Guid.NewGuid(), cancellationToken);
        }
        catch (MercadoLivreUnavailableException exception)
        {
            await FinishAsync(db, accountId, SyncStatuses.Failed, SyncErrors.MlUnavailable);
            metrics.ItemsUpserted(target.Upserted);
            metrics.SyncCompleted(command.Trigger, SyncResults.Failed, stopwatch.Elapsed);
            Failed(logger, accountId, command.Trigger, exception);
            throw;
        }
        catch (MercadoLivreReauthorizationRequiredException)
        {
            await db.SyncStates
                .Where(state => state.AccountId == accountId)
                .ExecuteUpdateAsync(set => set.SetProperty(state => state.Status, SyncStatuses.Paused), CancellationToken.None);
            metrics.ItemsUpserted(target.Upserted);
            metrics.SyncCompleted(command.Trigger, SyncResults.Paused, stopwatch.Elapsed);
            Paused(logger, accountId, command.Trigger);
            return;
        }
        catch (MercadoLivreAccountNotFoundException)
        {
            // A conta foi desconectada no meio: MercadoLivreAccountDisconnected apaga o resto.
            await FinishAsync(db, accountId, SyncStatuses.Idle, lastError: null);
            metrics.SyncCompleted(command.Trigger, SyncResults.Failed, stopwatch.Elapsed);
            return;
        }
        catch (Exception exception)
        {
            await FinishAsync(db, accountId, SyncStatuses.Failed, lastError: null);
            metrics.SyncCompleted(command.Trigger, SyncResults.Failed, stopwatch.Elapsed);
            Failed(logger, accountId, command.Trigger, exception);
            throw;
        }

        var completedAt = clock.GetUtcNow();
        var itemCount = await db.Listings.CountAsync(listing => listing.AccountId == accountId, cancellationToken);
        var manual = command.Trigger == SyncTriggers.Manual;
        await db.SyncStates
            .Where(state => state.AccountId == accountId && state.Status == SyncStatuses.Running)
            .ExecuteUpdateAsync(
                set =>
                {
                    set.SetProperty(state => state.Status, SyncStatuses.Idle)
                        .SetProperty(state => state.CompletedAt, completedAt)
                        .SetProperty(state => state.ItemCount, itemCount)
                        .SetProperty(state => state.LastError, (string?)null);
                    if (manual)
                    {
                        set.SetProperty(state => state.LastManualAt, completedAt);
                    }
                },
                cancellationToken);

        metrics.ItemsUpserted(target.Upserted);
        metrics.SyncCompleted(command.Trigger, SyncResults.Succeeded, stopwatch.Elapsed);
        Completed(logger, accountId, command.Trigger, itemCount, stopwatch.Elapsed.TotalMilliseconds);
    }

    // Encerra a execução em curso (sem cancelamento: o estado não pode ficar preso em running).
    private static Task<int> FinishAsync(ListingsModuleDbContext db, Guid accountId, string status, string? lastError) =>
        db.SyncStates
            .Where(state => state.AccountId == accountId && state.Status == SyncStatuses.Running)
            .ExecuteUpdateAsync(
                set => set.SetProperty(state => state.Status, status).SetProperty(state => state.LastError, lastError),
                CancellationToken.None);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Anúncios da conta {AccountId} sincronizados ({Trigger}): {ItemCount} anúncios em {DurationMs:F0} ms.")]
    private static partial void Completed(ILogger logger, Guid accountId, string trigger, int itemCount, double durationMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Sincronização dos anúncios da conta {AccountId} ({Trigger}) falhou.")]
    private static partial void Failed(ILogger logger, Guid accountId, string trigger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Sincronização dos anúncios da conta {AccountId} ({Trigger}) pausada: a conta precisa ser reconectada.")]
    private static partial void Paused(ILogger logger, Guid accountId, string trigger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "Sincronização dos anúncios da conta {AccountId} ({Trigger}) descartada: já em curso, pausada ou conta desconhecida.")]
    private static partial void Skipped(ILogger logger, Guid accountId, string trigger);
}

/// <summary>
/// Reconciliação pelo estado persistido (ADR-0010, emenda v1.3): enfileira <see cref="SyncListings"/> com
/// <see cref="SyncTriggers.Reconciliation"/> para cada conta que não está em <c>paused</c> e cuja última sincronização
/// (<c>greatest(completed_at, started_at)</c>, ignorando nulos: a conclusão ou a tentativa mais recente) é mais antiga que <see cref="ListingsOptions.ReconciliationInterval"/>.
/// Publicações repetidas (timer curto, várias instâncias) não duplicam trabalho.
/// </summary>
public static class ReconcileListingsHandler
{
    public static async Task<OutgoingMessages> Handle(
        ReconcileListings message,
        ListingsModuleDbContext db,
        TimeProvider clock,
        IOptions<ListingsOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        var due = clock.GetUtcNow() - options.Value.ReconciliationInterval;
        var accounts = await db.SyncStates.AsNoTracking()
            // greatest(completed_at, started_at) < due, ignorando nulos (conta sem nenhuma das duas não entra).
            .Where(state => state.Status != SyncStatuses.Paused
                && (state.CompletedAt != null || state.StartedAt != null)
                && (state.CompletedAt == null || state.CompletedAt < due)
                && (state.StartedAt == null || state.StartedAt < due))
            .OrderBy(state => state.AccountId)
            .Select(state => state.AccountId)
            .ToListAsync(cancellationToken);

        var messages = new OutgoingMessages();
        messages.AddRange(accounts.Select(accountId => new SyncListings(accountId, SyncTriggers.Reconciliation)));
        return messages;
    }
}
