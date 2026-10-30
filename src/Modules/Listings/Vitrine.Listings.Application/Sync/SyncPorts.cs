namespace Vitrine.Listings.Application.Sync;

/// <summary>Estado de sincronização de uma conta, como as telas e o pedido manual o leem.</summary>
public sealed record SyncStateView(
    Guid AccountId,
    Guid WorkspaceId,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastManualAt,
    int ItemCount,
    string? LastError);

/// <summary>Leitura dos estados de sincronização do workspace (schema <c>listings</c>).</summary>
public interface ISyncStates
{
    /// <summary>Contas do workspace conhecidas pelo Listings, em ordem estável.</summary>
    Task<IReadOnlyList<SyncStateView>> ListAsync(Guid workspaceId, CancellationToken cancellationToken);
}

/// <summary>Fila durável das sincronizações (Wolverine — ADR-0014).</summary>
public interface ISyncScheduler
{
    Task EnqueueAsync(SyncListings command, CancellationToken cancellationToken);
}

/// <summary>Configuração do Listings usada pelos casos de uso (<c>Listings:*</c>).</summary>
/// <param name="ManualSyncCooldown">Intervalo mínimo entre sincronizações manuais da conta (padrão 5 min).</param>
public sealed record ListingsSyncSettings(TimeSpan ManualSyncCooldown);
