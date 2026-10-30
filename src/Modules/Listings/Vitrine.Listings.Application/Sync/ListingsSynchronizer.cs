using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.Listings.Application.Sync;

/// <summary>
/// Destino da sincronização (marcar e varrer — SPEC-0022): grava os anúncios lidos com o <c>sync_run_id</c> da execução
/// e, no fim de uma execução completa, apaga os da conta que não foram vistos.
/// </summary>
public interface IListingsSyncTarget
{
    /// <summary>Insere ou atualiza os anúncios da conta, marcando-os com <paramref name="syncRunId"/>.</summary>
    Task UpsertAsync(Guid accountId, Guid syncRunId, IReadOnlyList<MercadoLivreItem> items, CancellationToken cancellationToken);

    /// <summary>Apaga os anúncios da conta cujo <c>sync_run_id</c> é diferente de <paramref name="syncRunId"/>.</summary>
    Task SweepAsync(Guid accountId, Guid syncRunId, CancellationToken cancellationToken);
}

/// <summary>
/// Algoritmo da importação de uma conta (SPEC-0022): para cada status de <see cref="Statuses"/>, busca os IDs
/// (<see cref="IMercadoLivreItems.SearchItemIdsAsync"/>), deduplica, lê em lotes (<see cref="IMercadoLivreItems.GetItemsAsync"/>)
/// e grava os encontrados; só varre quando todos os status terminam. As exceções do gateway propagam.
/// </summary>
public sealed class ListingsSynchronizer
{
    private readonly IMercadoLivreItems _items;
    private readonly IListingsSyncTarget _target;

    public ListingsSynchronizer(IMercadoLivreItems items, IListingsSyncTarget target)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(target);
        _items = items;
        _target = target;
    }

    /// <summary>Status importados, nesta ordem.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["active", "paused", "closed"];

    /// <summary>Sincroniza a conta na execução <paramref name="syncRunId"/>; devolve quantos anúncios distintos foram gravados.</summary>
    public async Task<int> SynchronizeAsync(Guid accountId, Guid syncRunId, CancellationToken cancellationToken)
    {
        // IDs já pedidos ao ML nesta execução (as páginas podem repetir IDs, e um anúncio pode mudar de status no meio).
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var stored = new HashSet<string>(StringComparer.Ordinal);

        foreach (var status in Statuses)
        {
            await foreach (var page in _items.SearchItemIdsAsync(accountId, status, cancellationToken))
            {
                var fresh = page.Where(id => !string.IsNullOrWhiteSpace(id) && requested.Add(id)).ToList();
                if (fresh.Count == 0)
                {
                    continue;
                }

                var results = await _items.GetItemsAsync(accountId, fresh, cancellationToken);
                var found = results
                    .OfType<MercadoLivreItemResult.Found>()
                    .Select(result => result.Item)
                    .Where(item => stored.Add(item.Id))
                    .ToList();
                if (found.Count > 0)
                {
                    await _target.UpsertAsync(accountId, syncRunId, found, cancellationToken);
                }
            }
        }

        await _target.SweepAsync(accountId, syncRunId, cancellationToken);
        return stored.Count;
    }
}
