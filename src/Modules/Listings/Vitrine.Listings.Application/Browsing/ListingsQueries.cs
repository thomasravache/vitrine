using Vitrine.Identity.PublicApi;
using Vitrine.Listings.Application.Sync;

namespace Vitrine.Listings.Application.Browsing;

/// <summary>Anúncio na listagem (contrato da SPEC-0022).</summary>
public sealed record ListingView(
    string ItemId,
    Guid AccountId,
    string Title,
    decimal Price,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    string? Permalink,
    string? ThumbnailUrl,
    DateTimeOffset LastUpdated);

/// <summary>Página da listagem; <paramref name="Next"/> nulo na última.</summary>
public sealed record ListingsPage(IReadOnlyList<ListingView> Items, ListingsCursor? Next);

/// <summary>Total de anúncios do workspace por situação no ML.</summary>
public sealed record ListingStatusCount(string Status, int Count);

/// <summary>Leituras da cópia local dos anúncios (schema <c>listings</c>), sempre restritas ao workspace.</summary>
public interface IListingsReadModel
{
    /// <summary>Página ordenada por <c>lastUpdated desc, itemId</c> (keyset), só do workspace.</summary>
    Task<ListingsPage> ListAsync(Guid workspaceId, ListingsFilter filter, CancellationToken cancellationToken);

    /// <summary>Quantidade de anúncios do workspace por status.</summary>
    Task<IReadOnlyList<ListingStatusCount>> CountByStatusAsync(Guid workspaceId, CancellationToken cancellationToken);
}

/// <summary><c>GET /api/v1/listings</c>: valida os parâmetros e lista os anúncios do workspace do usuário.</summary>
public sealed class ListListingsHandler(IWorkspaceMembership membership, IListingsReadModel listings)
{
    public async Task<ListListingsResult> HandleAsync(Guid userId, ListListingsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var member = await membership.GetAsync(userId, cancellationToken);
        if (member is null)
        {
            return new ListListingsResult.Forbidden();
        }

        var errors = ListListingsValidator.Validate(query, out var request);
        if (errors.Count > 0)
        {
            return new ListListingsResult.Invalid(errors);
        }

        return new ListListingsResult.Listed(await listings.ListAsync(member.WorkspaceId, ListingsFilter.From(request), cancellationToken));
    }
}

/// <summary>Resultado de <see cref="ListListingsHandler"/>.</summary>
public abstract record ListListingsResult
{
    private ListListingsResult()
    {
    }

    public sealed record Listed(ListingsPage Page) : ListListingsResult;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ListListingsResult;

    /// <summary>Usuário sem workspace.</summary>
    public sealed record Forbidden : ListListingsResult;
}

/// <summary>Resumo das telas: totais por status (<c>other</c> = fora de active/paused/closed) e estado por conta.</summary>
public sealed record ListingsSummary(int Total, int Active, int Paused, int Closed, int Other, IReadOnlyList<SyncStateView> Accounts);

/// <summary><c>GET /api/v1/listings/summary</c>: totais e estado de sincronização das contas do workspace do usuário.</summary>
public sealed class ListingsSummaryHandler(IWorkspaceMembership membership, IListingsReadModel listings, ISyncStates states)
{
    /// <summary>Resumo; nulo quando o usuário não pertence a nenhum workspace.</summary>
    public async Task<ListingsSummary?> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var member = await membership.GetAsync(userId, cancellationToken);
        if (member is null)
        {
            return null;
        }

        var counts = await listings.CountByStatusAsync(member.WorkspaceId, cancellationToken);
        var accounts = await states.ListAsync(member.WorkspaceId, cancellationToken);
        int Count(string status) => counts.Where(count => count.Status == status).Sum(count => count.Count);
        var total = counts.Sum(count => count.Count);
        var active = Count("active");
        var paused = Count("paused");
        var closed = Count("closed");
        return new ListingsSummary(total, active, paused, closed, total - active - paused - closed, accounts);
    }
}
