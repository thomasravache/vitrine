namespace Vitrine.Listings.Domain.Listings;

/// <summary>
/// Regra de frescor do anúncio (SPEC-0022): uma leitura do ML só substitui a cópia gravada quando não é mais antiga que
/// ela (<c>LastUpdated &gt;= ml_last_updated</c>); notificação fora de ordem é ignorada. Scaffold do contrato.
/// </summary>
public static class ListingUpdatePolicy
{
    /// <summary>
    /// Verdadeiro quando a leitura com <paramref name="incomingLastUpdated"/> deve ser aplicada sobre a cópia gravada com
    /// <paramref name="storedLastUpdated"/>.
    /// </summary>
    public static bool ShouldApply(DateTimeOffset storedLastUpdated, DateTimeOffset incomingLastUpdated) =>
        throw new NotImplementedException("SPEC-0022: implementado na fase Green.");
}
