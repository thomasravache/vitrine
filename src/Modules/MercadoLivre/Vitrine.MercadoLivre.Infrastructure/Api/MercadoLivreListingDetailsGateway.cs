using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <see cref="IMercadoLivreListingDetails"/> pelo cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0033).
/// Esqueleto do Red: a implementação vem no Green.
/// </summary>
internal sealed class MercadoLivreListingDetailsGateway : IMercadoLivreListingDetails
{
    public Task<MercadoLivreItemDetails?> GetItemDetailsAsync(Guid accountId, string itemId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0033: GetItemDetailsAsync ainda não implementado.");

    public Task<MercadoLivreCategory?> GetCategoryAsync(Guid accountId, string categoryId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0033: GetCategoryAsync ainda não implementado.");
}
