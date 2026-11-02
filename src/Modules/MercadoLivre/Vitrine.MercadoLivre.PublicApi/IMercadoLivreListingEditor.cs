namespace Vitrine.MercadoLivre.PublicApi;

/// <summary>
/// Publica no Mercado Livre as mudanças aprovadas de título, descrição e atributos (contrato v1 da SPEC-0033, ADR-0001,
/// ADR-0010). Relê o anúncio e a categoria, aplica <see cref="ListingChangeRules"/>, escreve no ML e devolve o
/// resultado por campo.
/// </summary>
public interface IMercadoLivreListingEditor
{
    /// <summary>
    /// Aplica <paramref name="changes"/> ao anúncio. Lança <see cref="MercadoLivreAccountNotFoundException"/>,
    /// <see cref="MercadoLivreReauthorizationRequiredException"/>, <see cref="MercadoLivreItemNotFoundException"/> (404 ou
    /// outro vendedor) e <see cref="MercadoLivreUnavailableException"/> (só quando a releitura inicial falha: nada foi
    /// escrito).
    /// </summary>
    Task<ListingChangeResult> ApplyAsync(Guid accountId, string itemId, ListingChanges changes, CancellationToken cancellationToken);
}

/// <summary>Mudanças pedidas; campo nulo = não mexer.</summary>
public sealed record ListingChanges(string? Title, string? Description, IReadOnlyList<AttributeChange>? Attributes);

/// <summary>Novo valor de um atributo (<c>value_id</c> para lista, <c>value_name</c> para valor livre).</summary>
public sealed record AttributeChange(string Id, string? ValueId, string? ValueName);

/// <summary>Resultado por campo; <paramref name="Attributes"/> por id de atributo enviado.</summary>
public sealed record ListingChangeResult(
    FieldResult? Title,
    FieldResult? Description,
    IReadOnlyDictionary<string, FieldResult> Attributes);

/// <summary>Resultado da escrita de um campo.</summary>
public abstract record FieldResult
{
    /// <summary>O ML aplicou a mudança.</summary>
    public sealed record Applied : FieldResult;

    /// <summary>O ML recusou ou falhou (<paramref name="Code"/> estável do contrato).</summary>
    public sealed record Rejected(string Code, string Message) : FieldResult;

    /// <summary>Não enviado: regra local ou passo anterior interrompido.</summary>
    public sealed record NotAttempted(string Reason) : FieldResult;
}
