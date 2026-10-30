using System.Globalization;
using Vitrine.MercadoLivre.Infrastructure.Api;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.UnitTests.MercadoLivre;

/// <summary>Paginação da busca de IDs: offset até 1000; acima, <c>search_type=scan</c> com <c>scroll_id</c> (SPEC-0021).</summary>
public sealed class ItemSearchPaginatorTests
{
    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Com_total_ate_1000_pagina_por_offset_ate_o_total()
    {
        // Dado uma busca com total = 250
        var ids = Ids("MLB1", 250);
        var search = new FakeSearch(request =>
        {
            Assert.False(request.Scan, "Com total ≤ 1000 a busca não pode usar scan.");
            Assert.Null(request.ScrollId);
            return new ItemSearchPage([.. ids.Skip(request.Offset).Take(100)], ids.Count, null);
        });

        // Quando o paginador percorre as páginas
        var pages = await CollectAsync(search);

        // Então pede os offsets 0, 100 e 200 e devolve os 250 IDs
        Assert.Equal([0, 100, 200], search.Requests.Select(request => request.Offset));
        Assert.Equal(ids, pages.SelectMany(page => page));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Com_total_exatamente_1000_continua_por_offset()
    {
        var ids = Ids("MLB2", 1000);
        var search = new FakeSearch(request =>
        {
            Assert.False(request.Scan, "Com total = 1000 a busca não pode usar scan.");
            return new ItemSearchPage([.. ids.Skip(request.Offset).Take(100)], ids.Count, null);
        });

        var pages = await CollectAsync(search);

        Assert.Equal(Enumerable.Range(0, 10).Select(page => page * 100), search.Requests.Select(request => request.Offset));
        Assert.Equal(ids, pages.SelectMany(page => page));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Com_total_acima_de_1000_descarta_a_primeira_pagina_e_segue_o_scroll_id_ate_results_vazio()
    {
        // Dado uma busca com total = 2300: a 1ª página por offset traz IDs que o scan devolve de novo
        var ids = Ids("MLB3", 2300);
        var offsetPage = new ItemSearchPage([.. Ids("MLB9", 100)], ids.Count, null);
        var search = new FakeSearch(request =>
        {
            if (!request.Scan)
            {
                return offsetPage;
            }

            var index = request.ScrollId is null ? 0 : int.Parse(request.ScrollId["scroll-".Length..], CultureInfo.InvariantCulture);
            var results = ids.Skip(index * 100).Take(100).ToList();

            // Última página: results vazio, mas ainda com scroll_id
            return new ItemSearchPage(results, 0, $"scroll-{index + 1}");
        });

        // Quando o paginador percorre
        var pages = await CollectAsync(search);

        // Então a 1ª página (offset) é descartada e o scan devolve todos os IDs
        Assert.Equal(ids, pages.SelectMany(page => page));
        Assert.DoesNotContain(pages.SelectMany(page => page), id => id.StartsWith("MLB9", StringComparison.Ordinal));

        // E a 1ª requisição é por offset 0; depois só scan, a primeira sem scroll_id e cada uma com o scroll_id anterior
        Assert.False(search.Requests[0].Scan);
        Assert.Equal(0, search.Requests[0].Offset);
        var scans = search.Requests.Skip(1).ToList();
        Assert.All(scans, request => Assert.True(request.Scan));
        Assert.Null(scans[0].ScrollId);
        for (var index = 1; index < scans.Count; index++)
        {
            Assert.Equal($"scroll-{index}", scans[index].ScrollId);
        }

        // 23 páginas cheias + a página vazia que encerra
        Assert.Equal(24, scans.Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Scan_com_o_mesmo_scroll_id_em_todas_as_paginas_e_results_diferentes_devolve_todos_os_IDs()
    {
        // Dado o scan do ML como um scroll do Elasticsearch: o mesmo scroll_id em todas as páginas, cada uma com os
        // próximos IDs, até results vazio
        var ids = Ids("MLB7", 2300);
        var scanPages = 0;
        var search = new FakeSearch(request =>
        {
            if (!request.Scan)
            {
                return new ItemSearchPage([.. ids.Take(100)], ids.Count, null);
            }

            var results = ids.Skip(scanPages++ * 100).Take(100).ToList();
            return new ItemSearchPage(results, 0, "scroll-fixo-elasticsearch");
        });

        // Quando o paginador percorre; então não lança e devolve todos os IDs
        var pages = await CollectAsync(search);
        Assert.Equal(ids, pages.SelectMany(page => page));

        // E segue o scroll_id (o mesmo) em toda página depois da 1ª scan: 23 cheias + a vazia que encerra
        var scans = search.Requests.Skip(1).ToList();
        Assert.Null(scans[0].ScrollId);
        Assert.All(scans.Skip(1), request => Assert.Equal("scroll-fixo-elasticsearch", request.ScrollId));
        Assert.Equal(24, scans.Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Scan_termina_quando_o_scroll_id_vem_nulo()
    {
        var search = new FakeSearch(request => request switch
        {
            { Scan: false } => new ItemSearchPage([.. Ids("MLB4", 100)], 1500, null),
            { ScrollId: null } => new ItemSearchPage(["MLB40001", "MLB40002"], 0, "scroll-a"),
            { ScrollId: "scroll-a" } => new ItemSearchPage(["MLB40003"], 0, null),
            _ => throw new InvalidOperationException($"Página não esperada depois do scroll_id nulo: {request}"),
        });

        var pages = await CollectAsync(search);

        Assert.Equal(["MLB40001", "MLB40002", "MLB40003"], pages.SelectMany(page => page));
        Assert.Equal(3, search.Requests.Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Scan_nao_entra_em_laco_quando_o_scroll_id_se_repete_com_results_vazio()
    {
        // Dado um scan que repete o mesmo scroll_id para sempre com results vazio
        var search = new FakeSearch(request => request switch
        {
            { Scan: false } => new ItemSearchPage([.. Ids("MLB5", 100)], 5000, null),
            { ScrollId: null } => new ItemSearchPage(["MLB50001"], 0, "scroll-fixo"),
            _ => new ItemSearchPage([], 0, "scroll-fixo"),
        });

        // Quando o paginador percorre (limite de tempo para provar que termina)
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var pages = await CollectAsync(search, timeout.Token);

        // Então termina na primeira página vazia
        Assert.Equal(["MLB50001"], pages.SelectMany(page => page));
        Assert.Equal(3, search.Requests.Count);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-02")]
    public async Task Scan_nao_entra_em_laco_quando_o_scroll_id_se_repete_com_results_nao_vazios()
    {
        // Dado um scan que devolve sempre o mesmo scroll_id com a mesma página não vazia
        var search = new FakeSearch(request => request switch
        {
            { Scan: false } => new ItemSearchPage([.. Ids("MLB6", 100)], 5000, null),
            _ => new ItemSearchPage(["MLB60001", "MLB60002"], 0, "scroll-repetido"),
        });

        // Quando o paginador percorre (com limite de tempo e de páginas)
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Exception? failure = null;
        try
        {
            await foreach (var page in ItemSearchPaginator.PaginateAsync(search.FetchAsync, timeout.Token))
            {
                Assert.NotNull(page);
            }
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            failure = exception;
        }

        // Então termina (normalmente ou com a falha do contrato) em poucas páginas, sem laço infinito
        Assert.False(
            failure is OperationCanceledException || search.Requests.Count >= 1000,
            $"O paginador entrou em laço com scroll_id repetido ({search.Requests.Count} páginas pedidas).");
        Assert.True(
            failure is null or MercadoLivreUnavailableException,
            $"Falha inesperada ao detectar o scroll_id repetido: {failure}");
        Assert.InRange(search.Requests.Count, 2, 10);
    }

    private static async Task<List<IReadOnlyList<string>>> CollectAsync(FakeSearch search, CancellationToken? cancellationToken = null)
    {
        var pages = new List<IReadOnlyList<string>>();
        await foreach (var page in ItemSearchPaginator.PaginateAsync(search.FetchAsync, cancellationToken ?? TestContext.Current.CancellationToken))
        {
            pages.Add(page);
            Assert.True(search.Requests.Count < 1000, "O paginador entrou em laço.");
        }

        return pages;
    }

    private static List<string> Ids(string prefix, int count) =>
        [.. Enumerable.Range(1, count).Select(index => string.Create(CultureInfo.InvariantCulture, $"{prefix}{index:D6}"))];

    /// <summary>Busca simulada: registra cada página pedida e responde pela função do teste.</summary>
    private sealed class FakeSearch(Func<ItemSearchPageRequest, ItemSearchPage> respond)
    {
        public List<ItemSearchPageRequest> Requests { get; } = [];

        public Task<ItemSearchPage> FetchAsync(ItemSearchPageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count > 1000)
            {
                throw new InvalidOperationException("O paginador entrou em laço (mais de 1000 páginas pedidas).");
            }

            return Task.FromResult(respond(request));
        }
    }
}
