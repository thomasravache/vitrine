namespace Vitrine.MercadoLivre.PublicApi;

/// <summary>
/// Leitura detalhada de um anúncio e das regras da categoria dele (contrato v1 da SPEC-0033, ADR-0001). Os tokens nunca
/// saem do módulo (ADR-0006). Lança <see cref="MercadoLivreAccountNotFoundException"/>,
/// <see cref="MercadoLivreReauthorizationRequiredException"/> e <see cref="MercadoLivreUnavailableException"/>.
/// </summary>
public interface IMercadoLivreListingDetails
{
    /// <summary>
    /// <c>GET /items/{id}</c> (seleção de campos) + <c>GET /items/{id}/description</c> (404 → <c>Description</c> nula).
    /// Nulo quando o anúncio não existe (404) ou é de outro vendedor (<c>seller_id</c> diferente do <c>ml_user_id</c> da
    /// conta).
    /// </summary>
    Task<MercadoLivreItemDetails?> GetItemDetailsAsync(Guid accountId, string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>GET /categories/{id}</c> + <c>GET /categories/{id}/attributes</c>, com cache de 24 h por categoria (só
    /// respostas 200). Nulo quando a categoria não existe (404).
    /// </summary>
    Task<MercadoLivreCategory?> GetCategoryAsync(Guid accountId, string categoryId, CancellationToken cancellationToken);
}

/// <summary>Anúncio completo do Mercado Livre (contrato v1 da SPEC-0033).</summary>
/// <param name="Id">ID do anúncio (ex.: <c>MLB1234567890</c>).</param>
/// <param name="SellerId">ID do vendedor no ML (int64).</param>
/// <param name="CategoryId">Categoria (ex.: <c>MLB1055</c>).</param>
/// <param name="Title">Título.</param>
/// <param name="SoldQuantity">Quantidade vendida.</param>
/// <param name="Status">Situação no ML (<c>active</c> | <c>paused</c> | <c>closed</c> ...).</param>
/// <param name="CatalogListing">Anúncio de catálogo.</param>
/// <param name="UserProductId">User product do anúncio, quando houver.</param>
/// <param name="Attributes">Atributos do anúncio.</param>
/// <param name="Description">Descrição em texto puro (<c>plain_text</c>); nula quando o anúncio não tem descrição.</param>
/// <param name="LastUpdated">Última alteração no ML.</param>
public sealed record MercadoLivreItemDetails(
    string Id,
    long SellerId,
    string CategoryId,
    string Title,
    int SoldQuantity,
    string Status,
    bool CatalogListing,
    string? UserProductId,
    IReadOnlyList<MercadoLivreItemAttribute> Attributes,
    string? Description,
    DateTimeOffset LastUpdated);

/// <summary>Atributo de um anúncio (<c>attributes[]</c> do item).</summary>
#pragma warning disable CA1711 // Nome do contrato (SPEC-0033): "atributo" do anúncio do ML, não um System.Attribute.
public sealed record MercadoLivreItemAttribute(string Id, string Name, string? ValueId, string? ValueName);

/// <summary>Categoria do ML com os limites e os atributos (contrato v1 da SPEC-0033).</summary>
/// <param name="Id">ID da categoria.</param>
/// <param name="Name">Nome.</param>
/// <param name="MaxTitleLength"><c>settings.max_title_length</c>.</param>
/// <param name="MaxDescriptionLength"><c>settings.max_description_length</c>; nulo quando ausente.</param>
/// <param name="Attributes">Atributos da categoria (<c>/categories/{id}/attributes</c>).</param>
public sealed record MercadoLivreCategory(
    string Id,
    string Name,
    int MaxTitleLength,
    int? MaxDescriptionLength,
    IReadOnlyList<MercadoLivreCategoryAttribute> Attributes);

/// <summary>Atributo de uma categoria do ML.</summary>
/// <param name="Id">ID do atributo (ex.: <c>COLOR</c>).</param>
/// <param name="Name">Nome.</param>
/// <param name="ValueType"><c>string</c> | <c>number</c> | <c>number_unit</c> | <c>boolean</c> | <c>list</c> ...</param>
/// <param name="Required"><c>tags.required</c> ou <c>tags.catalog_required</c>.</param>
/// <param name="ReadOnly"><c>tags.read_only</c>.</param>
/// <param name="Hidden"><c>tags.hidden</c>.</param>
/// <param name="Values">Valores permitidos; vazio = valor livre.</param>
/// <param name="AllowedUnits"><c>allowed_units[].id</c>.</param>
public sealed record MercadoLivreCategoryAttribute(
    string Id,
    string Name,
    string ValueType,
    bool Required,
    bool ReadOnly,
    bool Hidden,
    IReadOnlyList<MercadoLivreAttributeValue> Values,
    IReadOnlyList<string> AllowedUnits);
#pragma warning restore CA1711

/// <summary>Valor permitido de um atributo de categoria.</summary>
public sealed record MercadoLivreAttributeValue(string Id, string Name);
