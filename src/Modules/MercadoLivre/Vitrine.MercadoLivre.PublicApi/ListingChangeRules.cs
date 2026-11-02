namespace Vitrine.MercadoLivre.PublicApi;

/// <summary>
/// Regras locais do Mercado Livre para editar um anúncio (contrato v1 da SPEC-0033): pura, sem I/O, usada pelo editor
/// antes de chamar o ML e pelo módulo Listings para validar a sugestão da IA e os valores finais do vendedor.
/// </summary>
public static class ListingChangeRules
{
    /// <summary>Limite da descrição quando a categoria não informa <c>max_description_length</c>.</summary>
    public const int DefaultMaxDescriptionLength = 50000;

    /// <summary>Limite de um valor livre de atributo (não verificado no ML; conferir no go-live).</summary>
    public const int MaxFreeAttributeLength = 255;

    /// <summary>
    /// <c>TITLE_LOCKED_SOLD</c> (vendas), <c>TITLE_LOCKED_CATALOG</c> (catálogo), <c>TITLE_LOCKED_USER_PRODUCT</c> (user
    /// product), nessa precedência; nulo quando o título pode ser editado.
    /// </summary>
    public static string? TitleLockReason(MercadoLivreItemDetails item) =>
        throw new NotImplementedException("SPEC-0033: ListingChangeRules.TitleLockReason ainda não implementado.");

    /// <summary>
    /// Violações das <paramref name="changes"/>; chave <c>item</c> | <c>title</c> | <c>description</c> |
    /// <c>attributes.{id}</c>, valor = código do contrato.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Violations(
        MercadoLivreItemDetails item, MercadoLivreCategory category, ListingChanges changes) =>
        throw new NotImplementedException("SPEC-0033: ListingChangeRules.Violations ainda não implementado.");

    /// <summary>
    /// <c>ValueName</c> igual a um <c>Values[].Name</c> (sem diferenciar maiúsculas e acentos) vira o <c>ValueId</c>
    /// correspondente.
    /// </summary>
    public static AttributeChange Normalize(MercadoLivreCategoryAttribute spec, AttributeChange change) =>
        throw new NotImplementedException("SPEC-0033: ListingChangeRules.Normalize ainda não implementado.");
}
