namespace Vitrine.Listings.Application.Sync;

/// <summary>
/// Comando durável da sincronização de uma conta (SPEC-0022): importação completa dos anúncios
/// (<c>active</c>, <c>paused</c>, <c>closed</c>), uma por conta por vez.
/// </summary>
/// <param name="AccountId">Conta do Mercado Livre.</param>
/// <param name="Trigger">Origem: <see cref="SyncTriggers.Connected"/>, <see cref="SyncTriggers.Manual"/> ou
/// <see cref="SyncTriggers.Reconciliation"/>.</param>
public sealed record SyncListings(Guid AccountId, string Trigger);

/// <summary>
/// Reconciliação (SPEC-0022, ADR-0010): enfileira <see cref="SyncListings"/> com <see cref="SyncTriggers.Reconciliation"/>
/// para cada conta conhecida que não está em <c>paused</c>. Publicado a cada <c>Listings:ReconciliationInterval</c>.
/// </summary>
public sealed record ReconcileListings;

/// <summary>Origens da sincronização (tag <c>trigger</c> das métricas <c>vitrine.listings.sync.*</c>).</summary>
public static class SyncTriggers
{
    public const string Connected = "connected";
    public const string Manual = "manual";
    public const string Reconciliation = "reconciliation";
}
