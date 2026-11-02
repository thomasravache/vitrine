using System.Globalization;
using Vitrine.MercadoLivreSimulator;

namespace Vitrine.UnitTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0034:UT-03 — massa inicial por vendedor (reset e <c>/__sim/sellers</c>): 1º ativo com vendas, 2º de catálogo,
/// categoria sintética, BRAND e ITEM_CONDITION preenchidos, COLOR vazio e descrição com "Cor: Preto" nos anúncios pares.
/// </summary>
public sealed class SimulatorSeedTests
{
    /// <summary>Categoria sintética de cada produto da massa (SPEC-0034, seção 6).</summary>
    private static readonly Dictionary<string, string> CategoryByProduct = new(StringComparer.Ordinal)
    {
        ["Fone de ouvido bluetooth"] = "MLB90001",
        ["Capa para celular de silicone"] = "MLB90001",
        ["Luminária de mesa LED"] = "MLB90001",
        ["Teclado sem fio"] = "MLB90001",
        ["Carregador USB-C 20 W"] = "MLB90001",
        ["Mouse óptico"] = "MLB90001",
        ["Garrafa térmica 1 L"] = "MLB90002",
        ["Caneca de cerâmica"] = "MLB90002",
        ["Organizador de gaveta"] = "MLB90002",
        ["Mochila para notebook"] = "MLB90003",
    };

    private static SimulatorState NewState() => new(new FixedClock());

    /// <summary>Número do anúncio (parte numérica do id, ex.: 9000000001 em MLB9000000001).</summary>
    private static long Number(string itemId) => long.Parse(itemId.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Product(ItemInfo item) => item.Title.Split(" — ")[0];

    private static List<ItemInfo> Items(SimulatorState state, long sellerId, string? status) =>
        [.. state.ItemIds(sellerId, status).Select(id => state.FindItem(id)!)];

    private static void AssertSellerSeed(SimulatorState state, long sellerId)
    {
        // 1º ativo com vendas, 2º de catálogo sem vendas, demais ativos sem vendas e fora do catálogo
        var active = Items(state, sellerId, "active");
        Assert.True(active.Count >= 3, $"Vendedor {sellerId} com {active.Count} ativos (o teste pede ao menos 3).");
        Assert.Equal(12, active[0].SoldQuantity);
        Assert.False(active[0].CatalogListing, $"{active[0].Id}: o 1º ativo não é de catálogo.");
        Assert.Equal(0, active[1].SoldQuantity);
        Assert.True(active[1].CatalogListing, $"{active[1].Id}: o 2º ativo deveria ser de catálogo.");
        foreach (var item in active.Skip(2))
        {
            Assert.True(item.SoldQuantity == 0, $"{item.Id}: ativo além do 2º com sold_quantity {item.SoldQuantity}.");
            Assert.False(item.CatalogListing, $"{item.Id}: ativo além do 2º com catalog_listing true.");
        }

        // Pausados e encerrados mantêm as vendas atuais (índice % 37)
        foreach (var item in Items(state, sellerId, "paused").Concat(Items(state, sellerId, "closed")))
        {
            Assert.Equal((int)(Number(item.Id) % 1_000 % 37), item.SoldQuantity);
        }

        foreach (var item in Items(state, sellerId, null))
        {
            // Categoria sintética existente, a do produto
            Assert.True(SimulatorCategories.Find(item.CategoryId) is not null, $"{item.Id}: categoria '{item.CategoryId}' não existe.");
            Assert.Equal(CategoryByProduct[Product(item)], item.CategoryId);

            // BRAND e ITEM_CONDITION preenchidos; COLOR e MODEL vazios (obrigatório faltando)
            Assert.Contains(item.Attributes, a => a.Id == "BRAND" && a.ValueName == "Marca Simulada");
            Assert.Contains(item.Attributes, a => a.Id == "ITEM_CONDITION" && a.ValueName == "Novo");
            Assert.DoesNotContain(item.Attributes, a => a.Id == "COLOR" && (a.ValueId is not null || !string.IsNullOrEmpty(a.ValueName)));
            Assert.DoesNotContain(item.Attributes, a => a.Id == "MODEL" && (a.ValueId is not null || !string.IsNullOrEmpty(a.ValueName)));

            // Número par: descrição com o dado que permite derivar COLOR; ímpar: sem descrição
            var description = state.FindDescription(item.Id);
            if (Number(item.Id) % 2 == 0)
            {
                Assert.True(description is not null, $"{item.Id} (par) sem descrição.");
                Assert.Equal($"{Product(item)} simulado. Cor: Preto. Marca: Marca Simulada. Anúncio de teste.", description.PlainText);
            }
            else
            {
                Assert.True(description is null, $"{item.Id} (ímpar) com descrição: {description?.PlainText}");
            }
        }
    }

    /// <summary>Retrato da massa sem as datas (que dependem do relógio).</summary>
    private static List<string> Snapshot(SimulatorState state) =>
        [.. state.Sellers().SelectMany(seller => state.ItemIds(seller.Id, null)).Select(id =>
        {
            var item = state.FindItem(id)!;
            var attributes = string.Join(";", item.Attributes.Select(a => $"{a.Id}={a.ValueId}/{a.ValueName}"));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{item.Id}|{item.SellerId}|{item.Title}|{item.Status}|{item.SoldQuantity}|{item.CatalogListing}|{item.CategoryId}|{attributes}|{state.FindDescription(id)?.PlainText}");
        })];

    [Fact]
    [Trait("spec", "SPEC-0034:UT-03")]
    public void Massa_do_reset_tem_vendas_catalogo_categoria_atributos_e_descricao_por_vendedor()
    {
        // Dado o estado depois do reset (VENDEDOR_TESTE e VENDEDOR_AR)
        var state = NewState();
        state.Reset();

        // Então cada vendedor da massa segue a regra
        AssertSellerSeed(state, SimulatorState.SellerTesteId);
        AssertSellerSeed(state, SimulatorState.SellerArId);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:UT-03")]
    public void Vendedor_criado_por_sim_sellers_tem_a_mesma_massa()
    {
        // Dado um vendedor criado como o POST /__sim/sellers faz (15 ativos, 6 pausados, 4 encerrados)
        var state = NewState();
        var seller = state.CreateSeller(null, Site.Find("MLB")!, active: 15, paused: 6, closed: 4);

        // Então a massa dele segue a mesma regra
        AssertSellerSeed(state, seller.Id);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:UT-03")]
    public void Segundo_reset_recria_a_mesma_massa()
    {
        // Dado a massa do reset, alterada por um vendedor criado
        var state = NewState();
        state.Reset();
        var first = Snapshot(state);
        state.CreateSeller(null, Site.Find("MLB")!, active: 3, paused: 0, closed: 0);

        // Quando o reset roda de novo
        state.Reset();

        // Então a massa é a mesma (ids, vendas, catálogo, categoria, atributos e descrição)
        Assert.Equal(first, Snapshot(state));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    }
}
