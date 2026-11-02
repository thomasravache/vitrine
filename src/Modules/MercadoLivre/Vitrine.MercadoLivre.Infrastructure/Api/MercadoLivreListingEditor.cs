using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <see cref="IMercadoLivreListingEditor"/> pelo cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0033).
/// Esqueleto do Red: a implementação vem no Green.
/// </summary>
internal sealed class MercadoLivreListingEditor : IMercadoLivreListingEditor
{
    public Task<ListingChangeResult> ApplyAsync(Guid accountId, string itemId, ListingChanges changes, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0033: ApplyAsync ainda não implementado.");
}
