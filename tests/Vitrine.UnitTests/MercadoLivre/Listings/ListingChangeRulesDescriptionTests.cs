using Vitrine.MercadoLivre.PublicApi;
using static Vitrine.UnitTests.MercadoLivre.Listings.ListingSamples;

namespace Vitrine.UnitTests.MercadoLivre.Listings;

/// <summary>Regras da descrição em <see cref="ListingChangeRules"/> (SPEC-0033): texto puro, sem contato e no limite.</summary>
public sealed class ListingChangeRulesDescriptionTests
{
    [Theory]
    [Trait("spec", "SPEC-0033:UT-02")]
    [InlineData("Fone <b>bluetooth</b> sem fio.")]
    [InlineData("Fone<br/>bluetooth")]
    [InlineData("Fone&nbsp;bluetooth sem fio.")]
    [InlineData("Fone d&#39;água resistente.")]
    [InlineData("Som incrível 😀")]
    [InlineData("Qualidade garantida ❤️")]
    [InlineData("Aprovado ✔️ pelos clientes")]
    public void HTML_entidade_ou_emoji_da_DESCRIPTION_INVALID(string description)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Description(description));

        Assert.Equal("DESCRIPTION_INVALID", violations.GetValueOrDefault("description"));
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-02")]
    [InlineData("Dúvidas: contato@lojateste.com.br")]
    [InlineData("Ligue (11) 98765-4321 para mais detalhes.")]
    [InlineData("Ligue 11 98765-4321 para mais detalhes.")]
    [InlineData("Ligue +55 11 98765-4321 para mais detalhes.")]
    [InlineData("Ligue 11987654321 para mais detalhes.")]
    [InlineData("Ligue 98765-4321 para mais detalhes.")]
    [InlineData("Ligue 987654321 para mais detalhes.")]
    [InlineData("98765-4321")]
    [InlineData("987654321")]
    [InlineData("ligue 3456-7890")]
    [InlineData("Tel: 3456-7890")]
    [InlineData("tel: 3456 7890")]
    [InlineData("Ligue 3456 7890")]
    [InlineData("Veja mais em https://lojateste.com.br/fone")]
    [InlineData("Veja mais em www.lojateste.com.br")]
    [InlineData("Chame no WhatsApp para negociar.")]
    [InlineData("chame no whatsapp")]
    public void Email_telefone_URL_ou_whatsapp_da_DESCRIPTION_CONTACT(string description)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Description(description));

        Assert.Equal("DESCRIPTION_CONTACT", violations.GetValueOrDefault("description"));
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-02")]
    [InlineData("Compatível com Gol 2008-2012")]
    [InlineData("Resolução 1920 1080")]
    [InlineData("Lote 2024.2025")]
    [InlineData("Kit 1000 2000 W")]
    [InlineData("Ref. 0986 4945")]
    [InlineData("Peça 12345678")]
    [InlineData("novo.Com garantia")]
    [InlineData("3456-7890")]
    [InlineData("Para Celta 2008-2012")]
    [InlineData("Tela para modelos 2018-2020")]
    [InlineData("Capa celular modelos 2019-2021")]
    [InlineData("Fone de ouvido linha 2020-2021")]
    [InlineData("Pastilha Civic G10 2016-2021")]
    public void Numeros_e_textos_que_nao_sao_contato_sao_aceitos(string description)
    {
        // Emenda v1.1: anos, resolução, lotes, potências, códigos de peça, "novo.Com" (TLD só em minúsculas) e 8 dígitos
        // com máscara sem palavra de contato antes não são contato
        var violations = ListingChangeRules.Violations(Item(), Category(), Description(description));

        Assert.False(violations.ContainsKey("description"), $"Descrição sem contato recusada: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-02")]
    public void Acima_do_limite_da_categoria_da_DESCRIPTION_TOO_LONG()
    {
        // Dado a categoria com max_description_length = 100
        var category = Category(maxDescriptionLength: 100);

        // Quando descrições com 100 e 101 caracteres são avaliadas
        var accepted = ListingChangeRules.Violations(Item(), category, Description(new string('a', 100)));
        var rejected = ListingChangeRules.Violations(Item(), category, Description(new string('a', 101)));

        // Então 100 é aceita e 101 dá DESCRIPTION_TOO_LONG
        Assert.False(accepted.ContainsKey("description"), $"Descrição no limite não pode violar: {string.Join(", ", accepted)}");
        Assert.Equal("DESCRIPTION_TOO_LONG", rejected.GetValueOrDefault("description"));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-02")]
    public void Sem_limite_na_categoria_vale_50000()
    {
        // Dado a categoria sem max_description_length
        var category = Category(maxDescriptionLength: null);
        Assert.Equal(50000, ListingChangeRules.DefaultMaxDescriptionLength);

        // Quando descrições com 50.000 e 50.001 caracteres são avaliadas
        var accepted = ListingChangeRules.Violations(Item(), category, Description(new string('a', 50000)));
        var rejected = ListingChangeRules.Violations(Item(), category, Description(new string('a', 50001)));

        // Então 50.000 é aceita e 50.001 dá DESCRIPTION_TOO_LONG
        Assert.False(accepted.ContainsKey("description"));
        Assert.Equal("DESCRIPTION_TOO_LONG", rejected.GetValueOrDefault("description"));
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-02")]
    [InlineData("Fone de ouvido com áudio nítido e graves intensos.\nBateria de até 20 horas.\n\nGarantia de 3 meses.")]
    [InlineData("Conexão estável, ação rápida e fácil pareamento.")]
    [InlineData("Modelo BT-500, compatível com Android e iOS.")]
    public void Texto_com_acentos_e_quebras_de_linha_e_aceito(string description)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Description(description));

        Assert.False(violations.ContainsKey("description"), $"Descrição válida recusada: {string.Join(", ", violations)}");
    }
}
