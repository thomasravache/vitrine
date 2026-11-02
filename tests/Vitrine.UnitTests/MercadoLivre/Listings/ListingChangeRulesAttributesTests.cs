using Vitrine.MercadoLivre.PublicApi;
using static Vitrine.UnitTests.MercadoLivre.Listings.ListingSamples;

namespace Vitrine.UnitTests.MercadoLivre.Listings;

/// <summary>Regras e normalização dos atributos em <see cref="ListingChangeRules"/> (SPEC-0033).</summary>
public sealed class ListingChangeRulesAttributesTests
{
    [Fact]
    [Trait("spec", "SPEC-0033:UT-03")]
    public void Atributo_fora_da_categoria_da_ATTRIBUTE_UNKNOWN()
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Attributes(new AttributeChange("WARRANTY_TIME", null, "90 dias")));

        Assert.Equal("ATTRIBUTE_UNKNOWN", violations.GetValueOrDefault("attributes.WARRANTY_TIME"));
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-03")]
    [InlineData("ITEM_CONDITION", "2230581", null)]
    [InlineData("PRODUCT_FEATURES", null, "Resistente à água")]
    public void Atributo_read_only_ou_hidden_da_ATTRIBUTE_NOT_EDITABLE(string id, string? valueId, string? valueName)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Attributes(new AttributeChange(id, valueId, valueName)));

        Assert.Equal("ATTRIBUTE_NOT_EDITABLE", violations.GetValueOrDefault($"attributes.{id}"));
    }

    public static TheoryData<string, string?, string?> ValoresNaoPermitidos() => new()
    {
        { "COLOR", "99999", null },                                                       // fora de Values (id)
        { "COLOR", null, "Roxo" },                                                        // fora de Values (nome)
        { "BATTERY_LIFE", null, "20 dias" },                                              // unidade fora de AllowedUnits
        { "MODEL", null, null },                                                          // nem ValueId nem ValueName
        { "MODEL", null, "<b>BT-500</b>" },                                               // texto livre com HTML
        { "MODEL", null, new string('x', ListingChangeRules.MaxFreeAttributeLength + 1) }, // texto livre acima de 255
    };

    [Theory]
    [Trait("spec", "SPEC-0033:UT-03")]
    [MemberData(nameof(ValoresNaoPermitidos))]
    public void Valor_unidade_ausente_ou_texto_livre_invalido_da_ATTRIBUTE_VALUE_NOT_ALLOWED(string id, string? valueId, string? valueName)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Attributes(new AttributeChange(id, valueId, valueName)));

        Assert.Equal("ATTRIBUTE_VALUE_NOT_ALLOWED", violations.GetValueOrDefault($"attributes.{id}"));
    }

    public static TheoryData<string, string?, string?> ValoresPermitidos() => new()
    {
        { "COLOR", "52055", null },                                                   // id da lista
        { "COLOR", null, "preto" },                                                   // nome da lista sem diferenciar maiúsculas
        { "BATTERY_LIFE", null, "20 h" },                                             // unidade permitida
        { "MODEL", null, "BT-500 Pro" },                                              // texto livre
        { "MODEL", null, new string('x', ListingChangeRules.MaxFreeAttributeLength) }, // texto livre no limite
    };

    [Theory]
    [Trait("spec", "SPEC-0033:UT-03")]
    [MemberData(nameof(ValoresPermitidos))]
    public void Valor_permitido_nao_viola(string id, string? valueId, string? valueName)
    {
        var violations = ListingChangeRules.Violations(Item(), Category(), Attributes(new AttributeChange(id, valueId, valueName)));

        Assert.False(violations.ContainsKey($"attributes.{id}"), $"Valor permitido recusado: {string.Join(", ", violations)}");
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-03")]
    public void Cada_atributo_violado_tem_a_propria_chave()
    {
        // Dado um atributo válido e dois inválidos no mesmo pedido
        var violations = ListingChangeRules.Violations(Item(), Category(), Attributes(
            new AttributeChange("MODEL", null, "BT-500 Pro"),
            new AttributeChange("ITEM_CONDITION", "2230581", null),
            new AttributeChange("COLOR", "99999", null)));

        // Então só os inválidos aparecem, cada um com o seu código
        Assert.Equal(2, violations.Count);
        Assert.Equal("ATTRIBUTE_NOT_EDITABLE", violations["attributes.ITEM_CONDITION"]);
        Assert.Equal("ATTRIBUTE_VALUE_NOT_ALLOWED", violations["attributes.COLOR"]);
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-03")]
    [InlineData("preto", "52049")]
    [InlineData("PRETO", "52049")]
    [InlineData("Branco", "52055")]
    [InlineData("cafe", "52100")]
    [InlineData("CAFÉ", "52100")]
    public void ValueName_igual_a_um_valor_da_lista_sem_diferenciar_maiusculas_e_acentos_vira_ValueId(string valueName, string expectedId)
    {
        // Quando ValueName igual ao nome de um valor da lista é normalizado
        var normalized = ListingChangeRules.Normalize(Color, new AttributeChange("COLOR", null, valueName));

        // Então vira o ValueId correspondente
        Assert.Equal("COLOR", normalized.Id);
        Assert.Equal(expectedId, normalized.ValueId);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-03")]
    public void Texto_livre_e_nome_fora_da_lista_nao_mudam_na_normalizacao()
    {
        var free = new AttributeChange("MODEL", null, "BT-500 Pro");
        var unknown = new AttributeChange("COLOR", null, "Roxo");

        Assert.Equal(free, ListingChangeRules.Normalize(Model, free));
        Assert.Null(ListingChangeRules.Normalize(Color, unknown).ValueId);
    }
}
