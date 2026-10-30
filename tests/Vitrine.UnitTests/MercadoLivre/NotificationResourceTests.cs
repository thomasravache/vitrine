using Vitrine.MercadoLivre.Application.Notifications;

namespace Vitrine.UnitTests.MercadoLivre;

public sealed class NotificationResourceTests
{
    [Fact]
    [Trait("spec", "SPEC-0023:UT-01")]
    public void Resource_items_no_formato_documentado_produz_o_itemId()
    {
        // Dado /items/MLB123; quando interpretado para o tópico items
        var parsed = NotificationResource.TryGetItemId("/items/MLB123", out var itemId);

        // Então produz itemId = MLB123
        Assert.True(parsed);
        Assert.Equal("MLB123", itemId);
    }

    [Theory]
    [Trait("spec", "SPEC-0023:UT-01")]
    [InlineData("/items/MLB123?x")]
    [InlineData("/questions/1")]
    [InlineData("/items/abc")]
    [InlineData("/items/MLB")]
    [InlineData("/items/MLB123/description")]
    [InlineData("/items/mlb123")]
    [InlineData("items/MLB123")]
    [InlineData("/items/MLB123\n")]
    [InlineData("")]
    [InlineData(null)]
    public void Resource_fora_do_formato_nao_produz_itemId(string? resource)
    {
        // Dado um resource fora de ^/items/(MLB\d+)$; quando interpretado para o tópico items
        var parsed = NotificationResource.TryGetItemId(resource, out var itemId);

        // Então é inválido e não produz itemId
        Assert.False(parsed);
        Assert.Null(itemId);
    }
}
