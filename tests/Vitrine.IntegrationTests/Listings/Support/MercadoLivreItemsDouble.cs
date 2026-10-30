using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.Listings.Support;

/// <summary>
/// Dublê em memória de <see cref="IMercadoLivreItems"/>, fiel ao contrato SPEC-0021@1, registrado no host no lugar do
/// gateway real (seção 7.3 da SPEC-0022): catálogo por conta, busca em páginas pequenas que repetem o último ID da
/// página anterior (o contrato permite; o consumidor deduplica), leitura <c>Found</c>/<c>NotFound</c> e exceções do
/// contrato injetáveis por conta e status. Conta sem catálogo responde <see cref="MercadoLivreAccountNotFoundException"/>,
/// como o gateway real. <see cref="Hold"/> segura a busca para a sincronização ficar em <c>running</c>.
/// </summary>
internal sealed class MercadoLivreItemsDouble : IMercadoLivreItems
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, MercadoLivreItem>> _catalogs = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string Status), Exception> _searchFailures = new();
    private readonly ConcurrentDictionary<Guid, int> _runs = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string ItemId), int> _reads = new();
    private TaskCompletionSource _gate = Released();

    /// <summary>IDs por página da busca (o contrato fala em "até 100").</summary>
    public int PageSize { get; init; } = 2;

    /// <summary>Coloca (ou substitui) anúncios no catálogo da conta, criando-o se preciso.</summary>
    public void Put(Guid accountId, params MercadoLivreItem[] items)
    {
        var catalog = _catalogs.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, MercadoLivreItem>(StringComparer.Ordinal));
        foreach (var item in items)
        {
            catalog[item.Id] = item;
        }
    }

    /// <summary>Tira o anúncio do catálogo (a busca não o devolve; a leitura responde <c>NotFound</c>).</summary>
    public void Remove(Guid accountId, string itemId)
    {
        if (_catalogs.TryGetValue(accountId, out var catalog))
        {
            catalog.TryRemove(itemId, out _);
        }
    }

    public MercadoLivreItem Get(Guid accountId, string itemId) => _catalogs[accountId][itemId];

    /// <summary>A busca de <paramref name="status"/> da conta lança <paramref name="failure"/> até <see cref="ClearFailures"/>.</summary>
    public void FailSearch(Guid accountId, string status, Exception failure) => _searchFailures[(accountId, status)] = failure;

    public void ClearFailures(Guid accountId)
    {
        foreach (var key in _searchFailures.Keys.Where(key => key.AccountId == accountId))
        {
            _searchFailures.TryRemove(key, out _);
        }
    }

    /// <summary>Execuções de sincronização iniciadas para a conta (buscas de <c>active</c>, que toda execução faz uma vez).</summary>
    public int RunsStarted(Guid accountId) => _runs.TryGetValue(accountId, out var runs) ? runs : 0;

    /// <summary>Quantas vezes o anúncio foi pedido a <see cref="GetItemsAsync"/>.</summary>
    public int Reads(Guid accountId, string itemId) => _reads.TryGetValue((accountId, itemId), out var reads) ? reads : 0;

    /// <summary>Quantas leituras (<see cref="GetItemsAsync"/>) a conta recebeu, de qualquer anúncio.</summary>
    public int ReadsOf(Guid accountId) => _reads.Where(pair => pair.Key.AccountId == accountId).Sum(pair => pair.Value);

    /// <summary>Segura toda busca até o retorno ser descartado.</summary>
    public IDisposable Hold()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _gate, gate);
        return new Release(this, gate);
    }

    public async IAsyncEnumerable<IReadOnlyList<string>> SearchItemIdsAsync(
        Guid accountId, string status, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (status == "active")
        {
            _runs.AddOrUpdate(accountId, 1, (_, runs) => runs + 1);
        }

        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        if (_searchFailures.TryGetValue((accountId, status), out var failure))
        {
            throw failure;
        }

        if (!_catalogs.TryGetValue(accountId, out var catalog))
        {
            throw new MercadoLivreAccountNotFoundException(accountId);
        }

        var ids = catalog.Values.Where(item => item.Status == status).Select(item => item.Id).Order(StringComparer.Ordinal).ToList();
        for (var start = 0; start < ids.Count; start += PageSize)
        {
            var page = ids.Skip(start).Take(PageSize).ToList();
            if (start > 0)
            {
                page.Insert(0, ids[start - 1]);
            }

            yield return page;
        }
    }

    public Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(
        Guid accountId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_catalogs.TryGetValue(accountId, out var catalog))
        {
            throw new MercadoLivreAccountNotFoundException(accountId);
        }

        foreach (var id in itemIds)
        {
            _reads.AddOrUpdate((accountId, id), 1, (_, reads) => reads + 1);
        }

        IReadOnlyList<MercadoLivreItemResult> results = [.. itemIds.Select(id => catalog.TryGetValue(id, out var item)
            ? (MercadoLivreItemResult)new MercadoLivreItemResult.Found(item)
            : new MercadoLivreItemResult.NotFound(id))];
        return Task.FromResult(results);
    }

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private sealed class Release(MercadoLivreItemsDouble owner, TaskCompletionSource gate) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref owner._gate, Released());
            gate.TrySetResult();
        }
    }
}

/// <summary>Builder de anúncios do ML (IDs <c>MLB</c> com 10 dígitos, todos do mesmo tamanho).</summary>
internal static class ListingItem
{
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static long _next = 5_000_000_000;

    public static string NewId() => string.Create(CultureInfo.InvariantCulture, $"MLB{Interlocked.Increment(ref _next)}");

    public static MercadoLivreItem New(
        string status = "active", string? title = null, decimal price = 99.90m, DateTimeOffset? lastUpdated = null, string? id = null)
    {
        var itemId = id ?? NewId();
        return new MercadoLivreItem(
            itemId,
            SellerId: 3_200_000_001,
            Title: title ?? $"Anúncio de teste {itemId}",
            Price: price,
            CurrencyId: "BRL",
            AvailableQuantity: 7,
            SoldQuantity: 3,
            Status: status,
            Permalink: new Uri($"https://produto.mercadolivre.com.br/{itemId[..3]}-{itemId[3..]}-anuncio-_JM"),
            ThumbnailUrl: new Uri($"https://http2.mlstatic.com/D_{itemId}-I.jpg"),
            ListingTypeId: "gold_special",
            LastUpdated: lastUpdated ?? BaseTime);
    }
}
