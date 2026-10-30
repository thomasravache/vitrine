using Vitrine.Identity.PublicApi;
using Vitrine.Listings.Domain.Sync;

namespace Vitrine.Listings.Application.Sync;

/// <summary>
/// "Sincronizar agora" (<c>POST /api/v1/listings/sync</c> — SPEC-0022): enfileira <see cref="SyncListings"/> com
/// <see cref="SyncTriggers.Manual"/> para a conta pedida ou para todas as contas do workspace que não estão em
/// <c>paused</c>. Conta já em <c>running</c> não é reenfileirada (a sincronização em curso atende o pedido).
/// </summary>
public sealed class RequestSyncHandler(
    IWorkspaceMembership membership, ISyncStates states, ISyncScheduler scheduler, TimeProvider clock, ListingsSyncSettings settings)
{
    public async Task<RequestSyncResult> HandleAsync(Guid userId, Guid? accountId, CancellationToken cancellationToken)
    {
        var member = await membership.GetAsync(userId, cancellationToken);
        if (member is null)
        {
            return new RequestSyncResult.Forbidden();
        }

        var accounts = await states.ListAsync(member.WorkspaceId, cancellationToken);
        var now = clock.GetUtcNow();

        if (accountId is { } requested)
        {
            var account = accounts.FirstOrDefault(state => state.AccountId == requested);
            if (account is null)
            {
                return new RequestSyncResult.NotFound();
            }

            if (account.Status == SyncStatuses.Paused)
            {
                return new RequestSyncResult.ReauthorizationRequired();
            }

            if (!IsRunning(account, now)
                && ManualSyncCooldown.RetryAfterSeconds(account.LastManualAt, now, settings.ManualSyncCooldown) is { } retryAfter)
            {
                return new RequestSyncResult.TooSoon(retryAfter);
            }

            await EnqueueUnlessRunningAsync(account, now, cancellationToken);
            return new RequestSyncResult.Queued([account.AccountId]);
        }

        // Todas as contas do workspace: as em reautorização ficam de fora; as que ainda estão no intervalo mínimo também.
        var queued = new List<Guid>();
        int? soonest = null;
        foreach (var account in accounts.Where(state => state.Status != SyncStatuses.Paused))
        {
            if (!IsRunning(account, now)
                && ManualSyncCooldown.RetryAfterSeconds(account.LastManualAt, now, settings.ManualSyncCooldown) is { } retryAfter)
            {
                soonest = soonest is null ? retryAfter : Math.Min(soonest.Value, retryAfter);
                continue;
            }

            await EnqueueUnlessRunningAsync(account, now, cancellationToken);
            queued.Add(account.AccountId);
        }

        return queued.Count == 0 && soonest is { } wait
            ? new RequestSyncResult.TooSoon(wait)
            : new RequestSyncResult.Queued(queued);
    }

    // Running travado há mais de 1 h não conta: enfileira, e a nova execução assume (emenda v1.3).
    private static bool IsRunning(SyncStateView account, DateTimeOffset now) =>
        StaleRunningSync.IsRunning(account.Status, account.StartedAt, now);

    private Task EnqueueUnlessRunningAsync(SyncStateView account, DateTimeOffset now, CancellationToken cancellationToken) =>
        IsRunning(account, now)
            ? Task.CompletedTask
            : scheduler.EnqueueAsync(new SyncListings(account.AccountId, SyncTriggers.Manual), cancellationToken);
}

/// <summary>Resultado de <see cref="RequestSyncHandler"/>.</summary>
public abstract record RequestSyncResult
{
    private RequestSyncResult()
    {
    }

    /// <summary>202: contas atendidas (enfileiradas ou já em <c>running</c>).</summary>
    public sealed record Queued(IReadOnlyList<Guid> AccountIds) : RequestSyncResult;

    /// <summary>403: usuário sem workspace.</summary>
    public sealed record Forbidden : RequestSyncResult;

    /// <summary>404: conta inexistente ou de outro workspace (não revela existência).</summary>
    public sealed record NotFound : RequestSyncResult;

    /// <summary>409: a conta está em <c>paused</c> por reautorização.</summary>
    public sealed record ReauthorizationRequired : RequestSyncResult;

    /// <summary>429: a última sincronização manual concluiu há menos que o intervalo mínimo.</summary>
    public sealed record TooSoon(int RetryAfterSeconds) : RequestSyncResult;
}
