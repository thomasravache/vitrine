using System.Diagnostics.Metrics;

namespace Vitrine.Listings.Infrastructure.Diagnostics;

/// <summary>
/// Métricas do módulo Listings (SPEC-0022, plano de rollout): duração e resultado das sincronizações, por origem, e
/// anúncios gravados; notificações de anúncio descartadas (<c>vitrine.listings.item_changes</c>, SPEC-0029).
/// </summary>
public sealed class ListingsMetrics
{
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _runs;
    private readonly Counter<long> _itemsUpserted;
    private readonly Counter<long> _itemChanges;

    public ListingsMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(ListingsModule.MeterName);
        _duration = meter.CreateHistogram<double>(
            "vitrine.listings.sync.duration", unit: "s", description: "Duração da sincronização dos anúncios de uma conta.");
        _runs = meter.CreateCounter<long>(
            "vitrine.listings.sync.runs", unit: "{run}", description: "Sincronizações de anúncios concluídas, por origem e resultado.");
        _itemsUpserted = meter.CreateCounter<long>(
            "vitrine.listings.items_upserted", unit: "{item}", description: "Anúncios inseridos ou atualizados na cópia local.");
        _itemChanges = meter.CreateCounter<long>(
            "vitrine.listings.item_changes", unit: "{notification}", description: "Notificações de anúncio alterado, por resultado.");
    }

    /// <summary>Uma notificação de anúncio alterado terminou em <paramref name="result"/> (<see cref="ItemChangeResults"/>).</summary>
    public void ItemChange(string result) =>
        _itemChanges.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>Registra uma sincronização encerrada (<paramref name="result"/>: <c>succeeded</c> | <c>failed</c> | <c>paused</c>).</summary>
    public void SyncCompleted(string trigger, string result, TimeSpan duration)
    {
        var triggerTag = new KeyValuePair<string, object?>("trigger", trigger);
        _duration.Record(duration.TotalSeconds, triggerTag);
        _runs.Add(1, triggerTag, new KeyValuePair<string, object?>("result", result));
    }

    public void ItemsUpserted(int count)
    {
        if (count > 0)
        {
            _itemsUpserted.Add(count);
        }
    }
}

/// <summary>Valores da tag <c>result</c> de <c>vitrine.listings.sync.runs</c>.</summary>
public static class SyncResults
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Paused = "paused";
}

/// <summary>Valores da tag <c>result</c> de <c>vitrine.listings.item_changes</c>.</summary>
public static class ItemChangeResults
{
    /// <summary>O anúncio relido é de outro vendedor (notificação forjada — SPEC-0029): nada é gravado.</summary>
    public const string ForeignItem = "foreign_item";
}
