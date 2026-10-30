namespace Vitrine.Listings.Infrastructure;

/// <summary>Configuração <c>Listings:*</c> (SPEC-0022).</summary>
public sealed class ListingsOptions
{
    public const string SectionName = "Listings";

    /// <summary>Intervalo da reconciliação de todas as contas não <c>paused</c> (padrão 24 h; ADR-0010).</summary>
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Intervalo mínimo entre sincronizações manuais da mesma conta (padrão 5 min).</summary>
    public TimeSpan ManualSyncCooldown { get; set; } = TimeSpan.FromMinutes(5);
}
