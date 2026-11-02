using Vitrine.MercadoLivre.PublicApi;
using static Vitrine.UnitTests.MercadoLivre.Listings.ListingSamples;

namespace Vitrine.UnitTests.MercadoLivre.Listings;

/// <summary>Regras do título e do estado do anúncio em <see cref="ListingChangeRules"/> (SPEC-0033).</summary>
public sealed class ListingChangeRulesTitleTests
{
    [Theory]
    [Trait("spec", "SPEC-0033:UT-01")]
    [InlineData(1, true, "MLBU100000001", "TITLE_LOCKED_SOLD")]
    [InlineData(3, false, null, "TITLE_LOCKED_SOLD")]
    [InlineData(0, true, "MLBU100000001", "TITLE_LOCKED_CATALOG")]
    [InlineData(0, true, null, "TITLE_LOCKED_CATALOG")]
    [InlineData(0, false, "MLBU100000001", "TITLE_LOCKED_USER_PRODUCT")]
    public void Titulo_travado_por_vendas_catalogo_ou_user_product_nessa_precedencia(
        int soldQuantity, bool catalogListing, string? userProductId, string expected)
    {
        // Dado um anúncio com vendas, de catálogo ou com user product
        var item = Item(soldQuantity, catalogListing, userProductId);

        // Quando o título é avaliado
        var reason = ListingChangeRules.TitleLockReason(item);
        var violations = ListingChangeRules.Violations(item, Category(), Title("Fone Bluetooth Sem Fio Preto Novo"));

        // Então o motivo é o de maior precedência, também em Violations["title"]
        Assert.Equal(expected, reason);
        Assert.Equal(expected, violations.GetValueOrDefault("title"));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-01")]
    public void Titulo_sem_trava_nao_tem_motivo()
    {
        // Dado um anúncio sem vendas, fora do catálogo e sem user product
        var item = Item();

        // Quando o título é avaliado; então não há trava nem violação
        Assert.Null(ListingChangeRules.TitleLockReason(item));
        Assert.Empty(ListingChangeRules.Violations(item, Category(), Title("Fone Bluetooth Sem Fio Preto Novo")));
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-01")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   \t ")]
    public void Titulo_vazio_ou_so_espacos_da_TITLE_EMPTY(string title)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Title(title));

        Assert.Equal("TITLE_EMPTY", violations.GetValueOrDefault("title"));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-01")]
    public void Titulo_acima_do_limite_da_categoria_da_TITLE_TOO_LONG_e_no_limite_exato_e_aceito()
    {
        // Dado a categoria com max_title_length = 60
        var atLimit = new string('a', MaxTitleLength);
        var aboveLimit = new string('a', MaxTitleLength + 1);

        // Quando títulos com 60 e 61 caracteres são avaliados
        var accepted = ListingChangeRules.Violations(Item(), Category(), Title(atLimit));
        var rejected = ListingChangeRules.Violations(Item(), Category(), Title(aboveLimit));

        // Então 60 é aceito e 61 dá TITLE_TOO_LONG
        Assert.False(accepted.ContainsKey("title"), $"Título no limite exato não pode violar: {string.Join(", ", accepted)}");
        Assert.Equal("TITLE_TOO_LONG", rejected.GetValueOrDefault("title"));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-01")]
    public void Titulo_nulo_nao_e_avaliado_mesmo_com_trava()
    {
        // Dado um anúncio com vendas e mudanças sem título (nulo = não mexer)
        var violations = ListingChangeRules.Violations(Item(soldQuantity: 10), Category(), Description("Fone bluetooth sem fio."));

        // Então não há violação de título
        Assert.False(violations.ContainsKey("title"));
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-01")]
    [InlineData("paused")]
    [InlineData("closed")]
    [InlineData("under_review")]
    [InlineData("inactive")]
    public void Anuncio_fora_de_active_da_ITEM_NOT_ACTIVE_na_chave_item(string status)
    {
        // Dado um anúncio que não está ativo
        var item = Item(status: status);

        // Quando qualquer mudança é avaliada
        var violations = ListingChangeRules.Violations(
            item, Category(), new ListingChanges("Fone Bluetooth Sem Fio Preto Novo", "Fone bluetooth.", [new("MODEL", null, "BT-600")]));

        // Então a chave item tem ITEM_NOT_ACTIVE
        Assert.Equal("ITEM_NOT_ACTIVE", violations.GetValueOrDefault("item"));
    }
}
