using System.Diagnostics.CodeAnalysis;

namespace Vitrine.Listings.Application.Browsing;

/// <summary>Parâmetros crus de <c>GET /api/v1/listings</c> (contrato da SPEC-0022).</summary>
/// <param name="Status"><c>active</c> | <c>paused</c> | <c>closed</c> | <c>all</c>; ausente = <c>active</c>.</param>
/// <param name="AccountId">Filtro por conta (opcional).</param>
/// <param name="Q">Trecho do título (sem diferenciar maiúsculas) ou código exato <c>MLB\d+</c>; até 100 caracteres.</param>
/// <param name="Limit">Tamanho da página, 1..50; ausente = 20.</param>
/// <param name="Cursor">Cursor opaco de <see cref="ListingsCursor.Encode"/> (opcional).</param>
public sealed record ListListingsRequest(string? Status, Guid? AccountId, string? Q, int? Limit, string? Cursor);

/// <summary>Validação dos parâmetros da listagem (400 <c>VALIDATION_FAILED</c>). Scaffold do contrato.</summary>
public static class ListListingsValidator
{
    public const int DefaultLimit = 20;
    public const int MinLimit = 1;
    public const int MaxLimit = 50;
    public const int MaxQueryLength = 100;

    /// <summary>
    /// Erros por campo (<c>status</c>, <c>limit</c>, <c>q</c>, <c>cursor</c>); vazio quando os parâmetros são válidos.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(ListListingsRequest request) =>
        throw new NotImplementedException("SPEC-0022: implementado na fase Green.");
}

/// <summary>Consulta da listagem já validada e convertida. Scaffold do contrato.</summary>
/// <param name="Status"><c>active</c> | <c>paused</c> | <c>closed</c> | <c>all</c>.</param>
/// <param name="AccountId">Filtro por conta.</param>
/// <param name="TitleContains">Trecho do título (quando <c>q</c> não é um código).</param>
/// <param name="ItemId">Código exato (quando <c>q</c> casa <c>^MLB\d+$</c>).</param>
/// <param name="Limit">Tamanho da página.</param>
/// <param name="After">Posição depois da qual a página começa (keyset), ou nula na primeira página.</param>
public sealed record ListingsFilter(string Status, Guid? AccountId, string? TitleContains, string? ItemId, int Limit, ListingsCursor? After)
{
    /// <summary>Converte parâmetros válidos (sem erros em <see cref="ListListingsValidator.Validate"/>).</summary>
    public static ListingsFilter From(ListListingsRequest request) =>
        throw new NotImplementedException("SPEC-0022: implementado na fase Green.");
}

/// <summary>
/// Cursor keyset da listagem (<c>ml_last_updated desc, item_id</c>): a posição do último item da página. Codificado como
/// texto opaco; texto adulterado não decodifica. Scaffold do contrato.
/// </summary>
/// <param name="LastUpdated">Última alteração no ML do último item da página.</param>
/// <param name="ItemId">Código do último item da página.</param>
public sealed record ListingsCursor(DateTimeOffset LastUpdated, string ItemId)
{
    /// <summary>Texto opaco do cursor (seguro em query string).</summary>
    public string Encode() => throw new NotImplementedException("SPEC-0022: implementado na fase Green.");

    /// <summary>Decodifica um texto de <see cref="Encode"/>; falso para texto inválido ou adulterado.</summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out ListingsCursor? cursor) =>
        throw new NotImplementedException("SPEC-0022: implementado na fase Green.");
}
