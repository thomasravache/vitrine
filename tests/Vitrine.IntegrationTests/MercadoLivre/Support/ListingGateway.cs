using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Chama <see cref="IMercadoLivreListingDetails"/> e <see cref="IMercadoLivreListingEditor"/> (SPEC-0033) num escopo de
/// DI próprio, como o módulo Listings faria.
/// </summary>
internal static class ListingGateway
{
    public static async Task<MercadoLivreItemDetails?> GetItemDetailsAsync(
        ApiFactory factory, Guid accountId, string itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var details = scope.ServiceProvider.GetRequiredService<IMercadoLivreListingDetails>();
        return await details.GetItemDetailsAsync(accountId, itemId, cancellationToken);
    }

    public static async Task<MercadoLivreCategory?> GetCategoryAsync(
        ApiFactory factory, Guid accountId, string categoryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var details = scope.ServiceProvider.GetRequiredService<IMercadoLivreListingDetails>();
        return await details.GetCategoryAsync(accountId, categoryId, cancellationToken);
    }

    public static async Task<ListingChangeResult> ApplyAsync(
        ApiFactory factory, Guid accountId, string itemId, ListingChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var editor = scope.ServiceProvider.GetRequiredService<IMercadoLivreListingEditor>();
        return await editor.ApplyAsync(accountId, itemId, changes, cancellationToken);
    }

    /// <summary>
    /// Anúncio ativo da conta, sem vendas, com a categoria das fixtures e (se <paramref name="withDescription"/>) a
    /// descrição; as escritas respondem com sucesso. <paramref name="edit"/> ajusta o anúncio.
    /// </summary>
    public static string SetUpItem(
        MercadoLivreListingFake fake, SeededAccount account, bool withDescription = true, Action<System.Text.Json.Nodes.JsonObject>? edit = null)
    {
        ArgumentNullException.ThrowIfNull(fake);
        ArgumentNullException.ThrowIfNull(account);
        var itemId = MercadoLivreListingFake.NewItemId();
        fake.ItemResponds(ListingFixtures.Item(itemId, account.Seller.Id, edit));
        if (withDescription)
        {
            fake.DescriptionResponds(itemId, ListingFixtures.Description());
        }
        else
        {
            fake.DescriptionMissing(itemId);
        }

        fake.CategoryResponds(ListingFixtures.Category(), ListingFixtures.CategoryAttributes());
        return itemId;
    }
}
