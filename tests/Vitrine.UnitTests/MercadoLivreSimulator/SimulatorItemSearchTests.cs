using System.Globalization;
using Vitrine.MercadoLivreSimulator;

namespace Vitrine.UnitTests.MercadoLivreSimulator;

public sealed class SimulatorItemSearchTests
{
    // Dado 2.300 anúncios na massa (IDs MLB sintéticos).
    private static readonly IReadOnlyList<string> Ids =
        [.. Enumerable.Range(1, 2_300).Select(i => string.Create(CultureInfo.InvariantCulture, $"MLB{9_000_000_000L + i}"))];

    [Theory]
    [Trait("spec", "SPEC-0024:UT-03")]
    [InlineData(0, 50)]
    [InlineData(900, 100)]
    [InlineData(950, 50)]
    public void Busca_por_offset_dentro_de_1000_devolve_a_janela_e_o_paging(int offset, int limit)
    {
        // Quando a busca por offset fica com offset+limit <= 1000
        var page = new ItemSearch().ByOffset(Ids, offset, limit);

        // Então devolve exatamente a janela pedida e o paging com o total real
        Assert.NotNull(page);
        Assert.Equal(Ids.Skip(offset).Take(limit), page.Results);
        Assert.Equal(2_300, page.Total);
        Assert.Equal(offset, page.Offset);
        Assert.Equal(limit, page.Limit);
    }

    [Theory]
    [Trait("spec", "SPEC-0024:UT-03")]
    [InlineData(901, 100)]
    [InlineData(1000, 1)]
    [InlineData(1000, 50)]
    [InlineData(2000, 100)]
    public void Busca_por_offset_recusa_offset_mais_limit_acima_de_1000(int offset, int limit)
    {
        // Quando offset+limit > 1000 sem scan
        var page = new ItemSearch().ByOffset(Ids, offset, limit);

        // Então é recusada (o endpoint responde 400)
        Assert.Null(page);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-03")]
    public void Scan_devolve_todos_os_ids_em_paginas_encadeadas_por_scroll_id_terminando_com_scroll_id_null()
    {
        // Dado a busca scan com páginas de 100
        var search = new ItemSearch();
        var collected = new List<string>();
        string? scrollId = null;
        var pages = 0;

        // Quando segue o scroll_id até ele vir nulo
        while (true)
        {
            var page = search.Scan(Ids, scrollId, limit: 100);
            pages++;
            Assert.True(page.Results.Count <= 100, $"Página {pages} com {page.Results.Count} IDs (limite 100).");
            collected.AddRange(page.Results);
            if (page.ScrollId is null)
            {
                break;
            }

            Assert.True(page.Results.Count > 0, $"Página {pages} vazia com scroll_id não nulo: o scan não termina.");
            Assert.True(pages <= 30, "O scan passou de 30 páginas para 2.300 IDs em páginas de 100.");
            scrollId = page.ScrollId;
        }

        // Então todos os IDs vêm uma única vez e o scan termina com scroll_id null
        Assert.Equal(Ids.Count, collected.Count);
        Assert.Equal(Ids.Order(StringComparer.Ordinal), collected.Order(StringComparer.Ordinal));
        Assert.InRange(pages, 23, 24);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:UT-03")]
    public void Scans_simultaneos_tem_cursores_independentes()
    {
        // Dado dois scans começados na mesma busca
        var search = new ItemSearch();
        var a1 = search.Scan(Ids, null, limit: 100);
        var b1 = search.Scan(Ids, null, limit: 100);

        // Quando o primeiro avança
        var a2 = search.Scan(Ids, a1.ScrollId, limit: 100);

        // Então o segundo continua do seu próprio ponto
        var b2 = search.Scan(Ids, b1.ScrollId, limit: 100);
        Assert.Equal(a1.Results, b1.Results);
        Assert.Equal(a2.Results, b2.Results);
        Assert.Empty(a1.Results.Intersect(a2.Results));
    }
}
