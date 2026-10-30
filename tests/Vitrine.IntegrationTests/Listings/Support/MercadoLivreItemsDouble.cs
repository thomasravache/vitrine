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
    private readonly ConcurrentDictionary<Guid, long> _sellers = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string Status), Exception> _searchFailures = new();
    private readonly ConcurrentDictionary<Guid, int> _runs = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string ItemId), int> _reads = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string Status), int> _searches = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _statusGates = new(StringComparer.Ordinal);
    private TaskCompletionSource _gate = Released();
    private ReadHold? _nextRead;

    /// <summary>IDs por página da busca (o contrato fala em "até 100").</summary>
    public int PageSize { get; init; } = 2;

    /// <summary>
    /// Vendedor (<c>mlUserId</c>) da conta: os anúncios com o vendedor padrão de <see cref="ListingItem.New"/> postos no
    /// catálogo dela passam a ser desse vendedor, como no ML (SPEC-0029: anúncio de outro vendedor é recusado).
    /// </summary>
    public void SetSeller(Guid accountId, long mlUserId) => _sellers[accountId] = mlUserId;

    /// <summary>
    /// Coloca (ou substitui) anúncios no catálogo da conta, criando-o se preciso. Anúncio com o vendedor padrão vira do
    /// vendedor da conta (<see cref="SetSeller"/>); com outro <c>SellerId</c> explícito, fica como veio (anúncio alheio).
    /// </summary>
    public void Put(Guid accountId, params MercadoLivreItem[] items)
    {
        var catalog = _catalogs.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, MercadoLivreItem>(StringComparer.Ordinal));
        foreach (var item in items)
        {
            catalog[item.Id] = item.SellerId == ListingItem.DefaultSellerId && _sellers.TryGetValue(accountId, out var seller)
                ? item with { SellerId = seller }
                : item;
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

    /// <summary>Quantas buscas de <paramref name="status"/> a conta recebeu (contadas na chegada, antes de qualquer espera).</summary>
    public int SearchesStarted(Guid accountId, string status) =>
        _searches.TryGetValue((accountId, status), out var searches) ? searches : 0;

    /// <summary>Segura só as buscas de <paramref name="status"/> até o retorno ser descartado.</summary>
    public IDisposable HoldSearch(string status)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _statusGates[status] = gate;
        return new StatusRelease(this, status, gate);
    }

    /// <summary>
    /// Segura a próxima chamada a <see cref="GetItemsAsync"/> (de qualquer conta): o resultado é lido do catálogo na
    /// chegada e só é devolvido quando o retorno é descartado. <see cref="ReadHold.Reached"/> completa quando a chamada chega.
    /// </summary>
    public ReadHold HoldNextRead()
    {
        var hold = new ReadHold();
        Volatile.Write(ref _nextRead, hold);
        return hold;
    }

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

        _searches.AddOrUpdate((accountId, status), 1, (_, searches) => searches + 1);
        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        if (_statusGates.TryGetValue(status, out var statusGate))
        {
            await statusGate.Task.WaitAsync(cancellationToken);
        }

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

    public async Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(
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
        if (Interlocked.Exchange(ref _nextRead, null) is { } hold)
        {
            hold.Arrive(itemIds);
            await hold.Released.WaitAsync(cancellationToken);
        }

        return results;
    }

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private sealed class StatusRelease(MercadoLivreItemsDouble owner, string status, TaskCompletionSource gate) : IDisposable
    {
        public void Dispose()
        {
            owner._statusGates.TryRemove(status, out _);
            gate.TrySetResult();
        }
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

/// <summary>Leitura segurada por <see cref="MercadoLivreItemsDouble.HoldNextRead"/>.</summary>
internal sealed class ReadHold : IDisposable
{
    private readonly TaskCompletionSource<IReadOnlyList<string>> _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completa com os IDs pedidos quando a leitura chega (o resultado já foi lido do catálogo).</summary>
    public Task<IReadOnlyList<string>> Reached => _reached.Task;

    internal Task Released => _released.Task;

    internal void Arrive(IReadOnlyList<string> itemIds) => _reached.TrySetResult([.. itemIds]);

    /// <summary>Devolve o resultado lido na chegada.</summary>
    public void Dispose() => _released.TrySetResult();
}

/// <summary>Builder de anúncios do ML (IDs <c>MLB</c> com 10 dígitos, todos do mesmo tamanho).</summary>
internal static class ListingItem
{
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Vendedor dos anúncios criados aqui; <see cref="MercadoLivreItemsDouble.Put"/> o troca pelo da conta.</summary>
    public const long DefaultSellerId = 3_200_000_001;

    private static long _next = 5_000_000_000;

    public static string NewId() => string.Create(CultureInfo.InvariantCulture, $"MLB{Interlocked.Increment(ref _next)}");

    public static MercadoLivreItem New(
        string status = "active", string? title = null, decimal price = 99.90m, DateTimeOffset? lastUpdated = null, string? id = null)
    {
        var itemId = id ?? NewId();
        return new MercadoLivreItem(
            itemId,
            SellerId: DefaultSellerId,
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
