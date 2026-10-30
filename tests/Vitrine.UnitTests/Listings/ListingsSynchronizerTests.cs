using System.Globalization;
using System.Runtime.CompilerServices;
using Vitrine.Listings.Application.Sync;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.UnitTests.Listings;

/// <summary>
/// SPEC-0022:UT-01 — algoritmo da importação (marcar e varrer) com um dublê de <see cref="IMercadoLivreItems"/> fiel ao
/// contrato SPEC-0021@1 (páginas de IDs que podem repetir entre si, <c>Found</c>/<c>NotFound</c>, exceções) e um destino
/// em memória com a semântica do <c>upsert</c> por <c>(account_id, item_id)</c> e da varredura por <c>sync_run_id</c>.
/// </summary>
public sealed class ListingsSynchronizerTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("spec", "SPEC-0022:UT-01")]
    public async Task Segunda_execucao_sem_um_ID_e_com_outro_alterado_deixa_so_os_IDs_vistos_com_os_dados_novos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accountId = Guid.NewGuid();
        var items = new ItemsDouble();
        items.Add(Item("MLB1000000001", "active"), Item("MLB1000000002", "active"), Item("MLB1000000003", "active"));
        items.Add(Item("MLB1000000004", "paused"), Item("MLB1000000005", "closed"));
        var target = new InMemoryTarget();
        var synchronizer = new ListingsSynchronizer(items, target);

        // Dado uma 1ª execução com os 5 anúncios dos 3 status
        var firstRun = Guid.NewGuid();
        var firstCount = await synchronizer.SynchronizeAsync(accountId, firstRun, cancellationToken);
        Assert.Equal(5, firstCount);
        Assert.Equal(
            ["MLB1000000001", "MLB1000000002", "MLB1000000003", "MLB1000000004", "MLB1000000005"],
            target.Ids(accountId));

        // Quando a 2ª execução roda sem MLB1000000002 e com MLB1000000003 alterado (preço e last_updated)
        items.Remove("MLB1000000002");
        var changed = Item("MLB1000000003", "active") with { Price = 199.90m, LastUpdated = Base.AddHours(3) };
        items.Replace(changed);
        var secondRun = Guid.NewGuid();
        var secondCount = await synchronizer.SynchronizeAsync(accountId, secondRun, cancellationToken);

        // Então só restam os IDs vistos na 2ª execução, com os dados novos, todos marcados com a 2ª execução
        Assert.Equal(4, secondCount);
        Assert.Equal(["MLB1000000001", "MLB1000000003", "MLB1000000004", "MLB1000000005"], target.Ids(accountId));
        Assert.Equal(changed, target.Get(accountId, "MLB1000000003").Item);
        Assert.All(target.Rows(accountId), row => Assert.Equal(secondRun, row.SyncRunId));
        Assert.Equal(2, target.Sweeps.Count);
        Assert.Equal((accountId, secondRun), target.Sweeps[^1]);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:UT-01")]
    public async Task IDs_repetidos_entre_paginas_nao_duplicam_nem_a_leitura_nem_a_gravacao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accountId = Guid.NewGuid();

        // Dado a busca em páginas de 2 IDs que repetem o último ID da página anterior (permitido pelo contrato)
        var items = new ItemsDouble { PageSize = 2, RepeatAcrossPages = true };
        var ids = Enumerable.Range(1, 7).Select(n => string.Create(CultureInfo.InvariantCulture, $"MLB20000000{n:D2}")).ToList();
        items.Add([.. ids.Select(id => Item(id, "active"))]);
        items.Add(Item("MLB2000000099", "paused"));
        var target = new InMemoryTarget();

        // Quando a conta é sincronizada
        var count = await new ListingsSynchronizer(items, target).SynchronizeAsync(accountId, Guid.NewGuid(), cancellationToken);

        // Então cada ID é lido e gravado uma única vez, e a contagem é de anúncios distintos
        Assert.True(items.Pages.Count(page => page.Status == "active") > 3, "O dublê deveria ter paginado a busca de active.");
        Assert.Equal(8, count);
        Assert.Equal(target.UpsertedIds.Distinct(StringComparer.Ordinal).Count(), target.UpsertedIds.Count);
        Assert.Equal(items.RequestedIds.Distinct(StringComparer.Ordinal).Count(), items.RequestedIds.Count);
        Assert.Equal([.. ids, "MLB2000000099"], target.Ids(accountId));
    }

    [Theory]
    [Trait("spec", "SPEC-0022:UT-01")]
    [InlineData("paused", "unavailable")]
    [InlineData("closed", "unavailable")]
    [InlineData("closed", "reauthorization")]
    public async Task Falha_em_qualquer_status_propaga_a_excecao_e_nao_varre(string failingStatus, string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var accountId = Guid.NewGuid();
        var items = new ItemsDouble();
        items.Add(Item("MLB3000000001", "active"), Item("MLB3000000002", "paused"), Item("MLB3000000003", "closed"));
        var target = new InMemoryTarget();
        var synchronizer = new ListingsSynchronizer(items, target);
        await synchronizer.SynchronizeAsync(accountId, Guid.NewGuid(), cancellationToken);
        var sweepsBefore = target.Sweeps.Count;

        // Dado que MLB3000000001 sumiu do ML e a busca de um status falha no meio da 2ª execução
        items.Remove("MLB3000000001");
        items.FailSearch(failingStatus, failure == "unavailable"
            ? new MercadoLivreUnavailableException(accountId, "retries esgotados")
            : new MercadoLivreReauthorizationRequiredException(accountId));

        // Quando a execução roda
        var exception = await Record.ExceptionAsync(() => synchronizer.SynchronizeAsync(accountId, Guid.NewGuid(), cancellationToken));

        // Então a exceção do gateway propaga, não há varredura e nenhum anúncio gravado some
        Assert.True(
            failure == "unavailable" ? exception is MercadoLivreUnavailableException : exception is MercadoLivreReauthorizationRequiredException,
            $"Esperada a exceção do gateway ({failure}); recebida: {exception?.GetType().Name ?? "nenhuma"}.");
        Assert.Equal(sweepsBefore, target.Sweeps.Count);
        Assert.Equal(["MLB3000000001", "MLB3000000002", "MLB3000000003"], target.Ids(accountId));
    }

    private static MercadoLivreItem Item(string id, string status) => new(
        id,
        SellerId: 3_100_000_001,
        Title: $"Anúncio {id}",
        Price: 99.90m,
        CurrencyId: "BRL",
        AvailableQuantity: 10,
        SoldQuantity: 2,
        Status: status,
        Permalink: new Uri($"https://produto.mercadolivre.com.br/{id}"),
        ThumbnailUrl: new Uri($"https://http2.mlstatic.com/D_{id}-I.jpg"),
        ListingTypeId: "gold_special",
        LastUpdated: Base);

    /// <summary>Dublê em memória de <see cref="IMercadoLivreItems"/> (contrato SPEC-0021@1).</summary>
    private sealed class ItemsDouble : IMercadoLivreItems
    {
        private readonly Dictionary<string, MercadoLivreItem> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _searchFailures = new(StringComparer.Ordinal);

        public int PageSize { get; init; } = 100;

        public bool RepeatAcrossPages { get; init; }

        public List<(string Status, IReadOnlyList<string> Ids)> Pages { get; } = [];

        public List<string> RequestedIds { get; } = [];

        public void Add(params MercadoLivreItem[] items)
        {
            foreach (var item in items)
            {
                _items.Add(item.Id, item);
            }
        }

        public void Replace(MercadoLivreItem item) => _items[item.Id] = item;

        public void Remove(string id) => _items.Remove(id);

        public void FailSearch(string status, Exception exception) => _searchFailures[status] = exception;

        public async IAsyncEnumerable<IReadOnlyList<string>> SearchItemIdsAsync(
            Guid accountId, string status, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (_searchFailures.TryGetValue(status, out var failure))
            {
                throw failure;
            }

            var ids = _items.Values.Where(item => item.Status == status).Select(item => item.Id).Order(StringComparer.Ordinal).ToList();
            for (var start = 0; start < ids.Count; start += PageSize)
            {
                var page = ids.Skip(start).Take(PageSize).ToList();
                if (RepeatAcrossPages && start > 0)
                {
                    page.Insert(0, ids[start - 1]);
                }

                Pages.Add((status, page));
                yield return page;
            }
        }

        public Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(
            Guid accountId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
        {
            RequestedIds.AddRange(itemIds);
            IReadOnlyList<MercadoLivreItemResult> results = [.. itemIds.Select(id => _items.TryGetValue(id, out var item)
                ? (MercadoLivreItemResult)new MercadoLivreItemResult.Found(item)
                : new MercadoLivreItemResult.NotFound(id))];
            return Task.FromResult(results);
        }
    }

    /// <summary>Destino em memória: upsert por (conta, item) com o sync_run_id e varredura dos de outra execução.</summary>
    private sealed class InMemoryTarget : IListingsSyncTarget
    {
        private readonly Dictionary<(Guid AccountId, string ItemId), Row> _rows = [];

        public List<string> UpsertedIds { get; } = [];

        public List<(Guid AccountId, Guid SyncRunId)> Sweeps { get; } = [];

        public List<string> Ids(Guid accountId) =>
            [.. _rows.Keys.Where(key => key.AccountId == accountId).Select(key => key.ItemId).Order(StringComparer.Ordinal)];

        public List<Row> Rows(Guid accountId) => [.. _rows.Where(pair => pair.Key.AccountId == accountId).Select(pair => pair.Value)];

        public Row Get(Guid accountId, string itemId) => _rows[(accountId, itemId)];

        public Task UpsertAsync(Guid accountId, Guid syncRunId, IReadOnlyList<MercadoLivreItem> items, CancellationToken cancellationToken)
        {
            foreach (var item in items)
            {
                UpsertedIds.Add(item.Id);
                _rows[(accountId, item.Id)] = new Row(item, syncRunId);
            }

            return Task.CompletedTask;
        }

        public Task SweepAsync(Guid accountId, Guid syncRunId, CancellationToken cancellationToken)
        {
            Sweeps.Add((accountId, syncRunId));
            foreach (var key in _rows.Where(pair => pair.Key.AccountId == accountId && pair.Value.SyncRunId != syncRunId).Select(pair => pair.Key).ToList())
            {
                _rows.Remove(key);
            }

            return Task.CompletedTask;
        }

        public sealed record Row(MercadoLivreItem Item, Guid SyncRunId);
    }
}
