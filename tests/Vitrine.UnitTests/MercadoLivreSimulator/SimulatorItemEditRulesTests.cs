using Vitrine.MercadoLivreSimulator;

namespace Vitrine.UnitTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0034:UT-01 e UT-02 — regras de edição do ML imitadas pelo simulador (<see cref="ItemEditRules"/>): título
/// bloqueado com vendas ou catálogo, tamanho do título, atributos não editáveis ou fora da lista/unidade, item não
/// ativo, atomicidade e validação da descrição em texto puro.
/// </summary>
public sealed class SimulatorItemEditRulesTests
{
    private const string TitleNotUpdatable = "field_not_updatable";
    private const string TitleLength = "item.title.length.invalid";
    private const string AttributesInvalid = "item.attributes.invalid";
    private const string StatusNotModifiable = "item.status.not_modifiable";
    private const string DescriptionInvalid = "item.description.type.invalid";
    private const string DescriptionLength = "item.description.max_length";

    /// <summary><c>value_id</c> de "Preto" em <c>COLOR</c> (o mesmo da fixture <c>category-attributes.json</c>).</summary>
    private const string Preto = "52049";

    private static readonly DateTimeOffset Before = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static SimulatorCategory Category(string id)
    {
        var category = SimulatorCategories.Find(id);
        Assert.True(category is not null, $"Categoria sintética {id} não existe.");
        return category;
    }

    /// <summary>Anúncio sintético (BRAND e ITEM_CONDITION preenchidos, COLOR e MODEL vazios, como a massa).</summary>
    private static ItemInfo Item(string status = "active", int sold = 0, bool catalog = false, string categoryId = "MLB90001") =>
        new(
            "MLB9000000003",
            3_000_001_000,
            "Teclado sem fio — anúncio simulado 9000000003",
            99.9m,
            "BRL",
            10,
            sold,
            status,
            "https://produto.mercadolivre.com.br/MLB-9000000003-anuncio-simulado-_JM",
            "http://http2.mlstatic.com/D_NQ_NP_9000000003-MLB9000000003_102026-O.jpg",
            "https://http2.mlstatic.com/D_NQ_NP_9000000003-MLB9000000003_102026-O.jpg",
            "gold_pro",
            Before)
        {
            CategoryId = categoryId,
            CatalogListing = catalog,
            Attributes =
            [
                new ItemAttribute("BRAND", "Marca", null, "Marca Simulada"),
                new ItemAttribute("ITEM_CONDITION", "Condição do item", "2230284", "Novo"),
            ],
        };

    private static ItemUpdate Title(string title) => new(title, null);

    private static ItemUpdate Attribute(string id, string? valueId = null, string? valueName = null) =>
        new(null, [new AttributeUpdate(id, valueId, valueName)]);

    /// <summary>Erro 400 com uma <c>cause</c> de <paramref name="code"/>; devolve essa cause.</summary>
    private static ApiCause AssertRejected(ItemEditResult result, ItemInfo original, string code)
    {
        Assert.True(result.Error is not null, $"A edição deveria ser recusada com {code}, mas foi aceita.");
        Assert.Equal(400, result.Error.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Message));
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Error));
        var cause = result.Error.Cause.FirstOrDefault(c => c.Code == code);
        Assert.True(cause is not null, $"Esperava cause {code}; veio: {string.Join(", ", result.Error.Cause.Select(c => c.Code))}.");

        // Atomicidade: o anúncio devolvido é o original, sem mudança nenhuma.
        Assert.Equal(original, result.Item);
        Assert.Equal(original.Title, result.Item.Title);
        Assert.Equal(original.LastUpdated, result.Item.LastUpdated);
        Assert.Equal(original.Attributes, result.Item.Attributes);
        return cause;
    }

    [Fact]
    [Trait("spec", "SPEC-0034:UT-01")]
    public void Titulo_e_COLOR_validos_num_anuncio_ativo_sem_vendas_sao_aplicados_e_atualizam_last_updated()
    {
        // Dado um anúncio ativo, sem vendas e fora do catálogo
        var item = Item();

        // Quando o PUT traz título novo e COLOR da lista
        var result = ItemEditRules.Apply(
            item, Category("MLB90001"), new ItemUpdate("Teclado sem fio preto", [new AttributeUpdate("COLOR", Preto, null)]), Now);

        // Então é aceito: título e COLOR novos, os demais atributos mantidos e last_updated = agora
        Assert.True(result.Error is null, $"Edição válida recusada: {result.Error}");
        Assert.Equal("Teclado sem fio preto", result.Item.Title);
        Assert.Equal(Now, result.Item.LastUpdated);
        var color = Assert.Single(result.Item.Attributes, a => a.Id == "COLOR");
        Assert.Equal(Preto, color.ValueId);
        Assert.Equal("Preto", color.ValueName);
        Assert.Contains(result.Item.Attributes, a => a.Id == "BRAND" && a.ValueName == "Marca Simulada");
        Assert.Contains(result.Item.Attributes, a => a.Id == "ITEM_CONDITION" && a.ValueName == "Novo");

        // E o anúncio original (imutável) não foi tocado
        Assert.Equal("Teclado sem fio — anúncio simulado 9000000003", item.Title);
        Assert.Equal(Before, item.LastUpdated);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-01")]
    [InlineData(12, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void Titulo_com_vendas_ou_de_catalogo_da_field_not_updatable(int sold, bool catalog)
    {
        // Dado um anúncio com vendas ou de catálogo
        var item = Item(sold: sold, catalog: catalog);

        // Quando o PUT traz título
        var result = ItemEditRules.Apply(item, Category("MLB90001"), Title("Título novo permitido pelo tamanho"), Now);

        // Então o ML recusa com field_not_updatable ("title is not modifiable") e nada muda
        var cause = AssertRejected(result, item, TitleNotUpdatable);
        Assert.Contains("title is not modifiable", cause.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-01")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901")]
    public void Titulo_vazio_ou_acima_de_60_caracteres_da_erro_de_tamanho(string title)
    {
        var item = Item();

        var result = ItemEditRules.Apply(item, Category("MLB90001"), Title(title), Now);

        AssertRejected(result, item, TitleLength);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:UT-01")]
    public void Titulo_com_exatamente_60_caracteres_e_aceito()
    {
        var title = new string('a', 60);

        var result = ItemEditRules.Apply(Item(), Category("MLB90001"), Title(title), Now);

        Assert.True(result.Error is null, $"Título de 60 caracteres recusado: {result.Error}");
        Assert.Equal(title, result.Item.Title);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-01")]
    [InlineData("MLB90001", "ITEM_CONDITION", "2230284", null)] // read_only
    [InlineData("MLB90001", "PRODUCT_DATA_SOURCE", null, "manual")] // hidden
    [InlineData("MLB90001", "ATRIBUTO_INEXISTENTE", null, "x")] // desconhecido
    [InlineData("MLB90001", "COLOR", "99999999", null)] // value_id fora de values
    [InlineData("MLB90001", "CAPACITY", null, "500 mL")] // atributo de outra categoria
    [InlineData("MLB90002", "CAPACITY", null, "2 kg")] // unidade fora de allowed_units (mL, L)
    [InlineData("MLB90003", "LAPTOP_SIZE", null, "15 cm")] // unidade fora de allowed_units (pol)
    [InlineData("MLB90002", "MATERIAL", "99999999", null)] // value_id fora de values
    public void Atributo_nao_editavel_desconhecido_fora_da_lista_ou_da_unidade_da_item_attributes_invalid_com_o_id(
        string categoryId, string attributeId, string? valueId, string? valueName)
    {
        var item = Item(categoryId: categoryId);

        var result = ItemEditRules.Apply(item, Category(categoryId), Attribute(attributeId, valueId, valueName), Now);

        var cause = AssertRejected(result, item, AttributesInvalid);
        Assert.Contains(attributeId, cause.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-01")]
    [InlineData("MLB90002", "CAPACITY", "750 mL")]
    [InlineData("MLB90002", "CAPACITY", "1 L")]
    [InlineData("MLB90003", "LAPTOP_SIZE", "15 pol")]
    [InlineData("MLB90001", "MODEL", "TX-200")]
    [InlineData("MLB90001", "BRAND", "Outra Marca")]
    public void Atributo_editavel_com_valor_livre_ou_unidade_permitida_e_aceito(string categoryId, string attributeId, string valueName)
    {
        var result = ItemEditRules.Apply(Item(categoryId: categoryId), Category(categoryId), Attribute(attributeId, valueName: valueName), Now);

        Assert.True(result.Error is null, $"{attributeId}={valueName} recusado: {result.Error}");
        var attribute = Assert.Single(result.Item.Attributes, a => a.Id == attributeId);
        Assert.Equal(valueName, attribute.ValueName);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-01")]
    [InlineData("paused")]
    [InlineData("closed")]
    public void Item_nao_ativo_da_erro_de_status(string status)
    {
        // Dado um anúncio fora de active
        var item = Item(status: status);

        // Quando o PUT traz uma mudança que seria válida
        var result = ItemEditRules.Apply(item, Category("MLB90001"), Attribute("COLOR", Preto), Now);

        // Então o ML recusa pelo status e nada muda
        AssertRejected(result, item, StatusNotModifiable);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:UT-01")]
    public void Com_uma_violacao_entre_mudancas_validas_nada_e_aplicado()
    {
        // Dado um anúncio editável
        var item = Item();

        // Quando o PUT traz título e COLOR válidos e ITEM_CONDITION (read_only)
        var update = new ItemUpdate(
            "Teclado sem fio preto",
            [new AttributeUpdate("COLOR", Preto, null), new AttributeUpdate("ITEM_CONDITION", "2230284", null)]);
        var result = ItemEditRules.Apply(item, Category("MLB90001"), update, Now);

        // Então a edição inteira é recusada: nem o título nem COLOR mudam
        AssertRejected(result, item, AttributesInvalid);
        Assert.DoesNotContain(result.Item.Attributes, a => a.Id == "COLOR");
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-02")]
    [InlineData("<p>Fone de ouvido</p>")]
    [InlineData("Fone de ouvido <br> com bateria")]
    [InlineData("Fone &amp; capa")]
    [InlineData("Fone &#128512; novo")]
    [InlineData("Fone de ouvido \U0001F600")]
    public void Descricao_com_tag_entidade_HTML_ou_emoji_da_type_invalid(string description)
    {
        var error = ItemEditRules.ValidateDescription(Category("MLB90001"), description);

        Assert.True(error is not null, $"Descrição inválida aceita: {description}");
        Assert.Equal(400, error.Status);
        Assert.Contains(error.Cause, c => c.Code == DescriptionInvalid);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:UT-02")]
    public void Descricao_acima_de_50000_caracteres_da_erro_de_tamanho_e_ate_50000_e_aceita()
    {
        var category = Category("MLB90001");

        var tooLong = ItemEditRules.ValidateDescription(category, new string('a', 50_001));
        var atLimit = ItemEditRules.ValidateDescription(category, new string('a', 50_000));

        Assert.True(tooLong is not null, "Descrição de 50.001 caracteres aceita.");
        Assert.Equal(400, tooLong.Status);
        Assert.Contains(tooLong.Cause, c => c.Code == DescriptionLength);
        Assert.Null(atLimit);
    }

    [Theory]
    [Trait("spec", "SPEC-0034:UT-02")]
    [InlineData("Fone de ouvido sem fio, com áudio nítido.\nBateria de até 20 horas.\nAcompanha cabo de carregamento.")]
    [InlineData("Garrafa térmica de aço inoxidável: mantém a água gelada por 24 h (ação dupla).")]
    [InlineData("Fone de ouvido simulado. Cor: Preto. Marca: Marca Simulada. Anúncio de teste.")]
    public void Descricao_em_texto_puro_com_acentos_e_quebras_de_linha_e_aceita(string description)
    {
        Assert.Null(ItemEditRules.ValidateDescription(Category("MLB90001"), description));
    }
}
