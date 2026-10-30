using System.Text;
using Vitrine.Listings.Application.Browsing;

namespace Vitrine.UnitTests.Listings;

/// <summary>
/// SPEC-0022:UT-02 — parâmetros de <c>GET /api/v1/listings</c>: validação (<c>status</c>, <c>limit</c> 1..50, <c>q</c> até
/// 100), conversão em consulta (<c>q</c> = <c>MLB\d+</c> vira filtro por código) e cursor keyset
/// <c>(lastUpdated, itemId)</c> opaco, que recusa texto adulterado.
/// </summary>
public sealed class ListListingsValidatorTests
{
    private static readonly DateTimeOffset LastUpdated = new DateTimeOffset(2026, 10, 30, 14, 5, 7, TimeSpan.Zero).AddTicks(1_234_560);

    [Fact]
    [Trait("spec", "SPEC-0022:UT-02")]
    public void Sem_parametros_e_valido_e_vira_status_active_limit_20_sem_filtros()
    {
        var request = new ListListingsRequest(null, null, null, null, null);

        Assert.Empty(ListListingsValidator.Validate(request));
        var filter = ListingsFilter.From(request);
        Assert.Equal("active", filter.Status);
        Assert.Equal(20, filter.Limit);
        Assert.Null(filter.AccountId);
        Assert.Null(filter.TitleContains);
        Assert.Null(filter.ItemId);
        Assert.Null(filter.After);
    }

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [InlineData("active")]
    [InlineData("paused")]
    [InlineData("closed")]
    [InlineData("all")]
    public void Status_do_contrato_e_aceito(string status)
    {
        var request = new ListListingsRequest(status, null, null, null, null);

        Assert.Empty(ListListingsValidator.Validate(request));
        Assert.Equal(status, ListingsFilter.From(request).Status);
    }

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [InlineData("xyz")]
    [InlineData("under_review")]
    [InlineData("todos")]
    public void Status_fora_da_lista_gera_erro_so_em_status(string status) =>
        AssertOnlyFieldInvalid(new ListListingsRequest(status, null, null, null, null), "status");

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    [InlineData(1000)]
    public void Limit_fora_de_1_a_50_gera_erro_so_em_limit(int limit) =>
        AssertOnlyFieldInvalid(new ListListingsRequest(null, null, null, limit, null), "limit");

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [InlineData(1)]
    [InlineData(50)]
    public void Limit_nos_extremos_e_aceito(int limit)
    {
        var request = new ListListingsRequest(null, null, null, limit, null);

        Assert.Empty(ListListingsValidator.Validate(request));
        Assert.Equal(limit, ListingsFilter.From(request).Limit);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:UT-02")]
    public void Q_com_101_caracteres_gera_erro_so_em_q_e_com_100_e_aceito()
    {
        AssertOnlyFieldInvalid(new ListListingsRequest(null, null, new string('a', 101), null, null), "q");
        Assert.Empty(ListListingsValidator.Validate(new ListListingsRequest(null, null, new string('a', 100), null, null)));
    }

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [InlineData("MLB1234567890")]
    [InlineData("MLB1")]
    public void Q_no_formato_MLB_digitos_vira_filtro_por_codigo(string code)
    {
        var accountId = Guid.NewGuid();
        var request = new ListListingsRequest("all", accountId, code, 10, null);

        Assert.Empty(ListListingsValidator.Validate(request));
        var filter = ListingsFilter.From(request);
        Assert.Equal(code, filter.ItemId);
        Assert.Null(filter.TitleContains);
        Assert.Equal(accountId, filter.AccountId);
    }

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [InlineData("camiseta azul")]
    [InlineData("MLB")]
    [InlineData("MLB123 azul")]
    [InlineData("Kit MLB123")]
    public void Q_que_nao_e_codigo_vira_trecho_do_titulo(string text)
    {
        var filter = ListingsFilter.From(new ListListingsRequest(null, null, text, null, null));

        Assert.Equal(text, filter.TitleContains);
        Assert.Null(filter.ItemId);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:UT-02")]
    public void Cursor_codifica_e_decodifica_lastUpdated_e_itemId_em_texto_seguro_na_query_string()
    {
        var cursor = new ListingsCursor(LastUpdated, "MLB1234567890");

        var text = cursor.Encode();

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(text, Uri.EscapeDataString(text));
        Assert.True(ListingsCursor.TryDecode(text, out var decoded), $"Cursor recém-codificado não decodificou: {text}");
        Assert.Equal(LastUpdated, decoded.LastUpdated);
        Assert.Equal(LastUpdated.UtcTicks, decoded.LastUpdated.UtcTicks);
        Assert.Equal("MLB1234567890", decoded.ItemId);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:UT-02")]
    public void Cursor_valido_e_aceito_e_vira_a_posicao_da_pagina()
    {
        var cursor = new ListingsCursor(LastUpdated, "MLB1234567890");
        var request = new ListListingsRequest(null, null, null, null, cursor.Encode());

        Assert.Empty(ListListingsValidator.Validate(request));
        Assert.Equal(cursor, ListingsFilter.From(request).After);
    }

    [Theory]
    [Trait("spec", "SPEC-0022:UT-02")]
    [MemberData(nameof(TamperedCursors))]
    public void Cursor_adulterado_e_recusado(string tampered)
    {
        Assert.False(ListingsCursor.TryDecode(tampered, out _), $"Cursor adulterado foi aceito: {tampered}");
        AssertOnlyFieldInvalid(new ListListingsRequest(null, null, null, null, tampered), "cursor");
    }

    public static TheoryData<string> TamperedCursors()
    {
        var valid = new ListingsCursor(LastUpdated, "MLB1234567890").Encode();
        return new TheoryData<string>
        {
            "nao-e-um-cursor",
            "%%%",
            valid[..(valid.Length / 2)],
            valid + "A",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"x\":1}")).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        };
    }

    private static void AssertOnlyFieldInvalid(ListListingsRequest request, string field)
    {
        var errors = ListListingsValidator.Validate(request);
        Assert.Equal([field], errors.Keys);
        Assert.True(
            errors[field].Length > 0 && errors[field].All(message => !string.IsNullOrWhiteSpace(message)),
            $"O campo '{field}' precisa de ao menos uma mensagem de erro.");
    }
}
