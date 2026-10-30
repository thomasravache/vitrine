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
/// e grava os encontrados; só varre quando todos os status terminam. As exceções do gateway propagam. Scaffold do contrato.
/// </summary>
public sealed class ListingsSynchronizer
{
    public ListingsSynchronizer(IMercadoLivreItems items, IListingsSyncTarget target)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(target);
    }

    /// <summary>Status importados, nesta ordem.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["active", "paused", "closed"];

    /// <summary>Sincroniza a conta na execução <paramref name="syncRunId"/>; devolve quantos anúncios distintos foram gravados.</summary>
    public Task<int> SynchronizeAsync(Guid accountId, Guid syncRunId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0022: implementado na fase Green.");
}
