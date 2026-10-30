namespace Vitrine.Listings.Domain.Sync;

/// <summary>
/// Estado da sincronização de uma conta (SPEC-0022): uma linha por conta conhecida pelo Listings, criada ao conectar. A
/// linha em <see cref="SyncStatuses.Running"/> garante uma sincronização por conta por vez.
/// </summary>
public sealed class SyncState
{
    public SyncState(Guid accountId, Guid workspaceId)
    {
        AccountId = accountId;
        WorkspaceId = workspaceId;
        Status = SyncStatuses.Idle;
    }

    public Guid AccountId { get; private set; }

    public Guid WorkspaceId { get; private set; }

    /// <summary><see cref="SyncStatuses"/>.</summary>
    public string Status { get; private set; }

    /// <summary>Início da última sincronização.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Conclusão da última sincronização bem-sucedida (só gravada em sucesso).</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Conclusão da última sincronização manual bem-sucedida (base do intervalo mínimo entre pedidos).</summary>
    public DateTimeOffset? LastManualAt { get; private set; }

    /// <summary>Anúncios da conta depois da última sincronização bem-sucedida.</summary>
    public int ItemCount { get; private set; }

    /// <summary><see cref="SyncErrors"/> da última falha, ou nulo.</summary>
    public string? LastError { get; private set; }
}

/// <summary>Valores de <c>sync_states.status</c> (os mesmos do contrato da API).</summary>
public static class SyncStatuses
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Failed = "failed";

    /// <summary>A conta precisa ser reconectada (reautorização): não sincroniza até um novo <c>connected</c>.</summary>
    public const string Paused = "paused";
}

/// <summary>Valores de <c>sync_states.last_error</c>.</summary>
public static class SyncErrors
{
    public const string MlUnavailable = "ML_UNAVAILABLE";
}

/// <summary>Intervalo mínimo entre sincronizações manuais da mesma conta (SPEC-0022: 429 <c>SYNC_TOO_SOON</c>).</summary>
public static class ManualSyncCooldown
{
    /// <summary>
    /// Quanto falta para um novo pedido manual ser aceito, arredondado para cima em segundos (ao menos 1); nulo quando já
    /// pode.
    /// </summary>
    public static int? RetryAfterSeconds(DateTimeOffset? lastManualAt, DateTimeOffset now, TimeSpan cooldown)
    {
        if (lastManualAt is not { } last)
        {
            return null;
        }

        var remaining = last + cooldown - now;
        return remaining > TimeSpan.Zero ? Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) : null;
    }
}

/// <summary>
/// Sincronização travada (SPEC-0022, emenda v1.3): uma conta em <c>running</c> há mais que
/// <see cref="StaleRunningAfter"/> (ex.: processo derrubado no meio) conta como não-running — um novo pedido enfileira e
/// a nova execução assume.
/// </summary>
public static class StaleRunningSync
{
    /// <summary>Tempo máximo de uma sincronização em <c>running</c>.</summary>
    public static readonly TimeSpan StaleRunningAfter = TimeSpan.FromHours(1);

    /// <summary>Verdadeiro quando a conta está em <c>running</c> de fato (não travada).</summary>
    public static bool IsRunning(string status, DateTimeOffset? startedAt, DateTimeOffset now) =>
        status == SyncStatuses.Running && startedAt is { } started && started >= now - StaleRunningAfter;
}
