using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// SPEC-0023:CT-01 — contrato público <c>MercadoLivreItemChanged</c> v1 (seção 6), visto por um assinante depois de
/// uma notificação processada. A SPEC-0022 consome esse contrato.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationsContractTests(PostgresFixture postgres)
{
    /// <summary>Schema v1 de MercadoLivreItemChanged: campo JSON → tipo .NET.</summary>
    private static readonly Dictionary<string, Type> ItemChangedV1 = new(StringComparer.Ordinal)
    {
        ["accountId"] = typeof(Guid),
        ["workspaceId"] = typeof(Guid),
        ["mlUserId"] = typeof(long),
        ["itemId"] = typeof(string),
        ["notificationId"] = typeof(string),
        ["occurredAt"] = typeof(DateTimeOffset),
    };

    [Fact]
    [Trait("spec", "SPEC-0023:CT-01")]
    public async Task Assinante_recebe_MercadoLivreItemChanged_v1_com_os_campos_e_tipos_do_contrato()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // O tipo publicado em MercadoLivre.IntegrationEvents é exatamente o schema v1
        MercadoLivreEventsContractTests.AssertPublishedSchema(typeof(MercadoLivreItemChanged), ItemChangedV1);

        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // Dado uma notificação items processada (vendedor com user_id acima de Int32)
        Assert.True(account.Seller.Id > int.MaxValue);
        var itemId = MercadoLivreNotifications.NewItemId();
        var notification = MercadoLivreNotifications.Items(account.Seller.Id, itemId);
        var notificationId = notification["_id"]!.GetValue<string>();
        using var client = MercadoLivreNotifications.Client(factory);
        using (var response = await MercadoLivreNotifications.PostAsync(client, notification, cancellationToken))
        {
            await MercadoLivreNotifications.AssertEmptyOkAsync(response, cancellationToken);
        }

        // Quando o assinante de teste recebe MercadoLivreItemChanged
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ItemChangesFor(notificationId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreItemChanged da notificação",
            cancellationToken);

        // Então o payload valida contra o schema v1 (mlUserId int64, occurredAt UTC = sent), com os valores da notificação
        var payload = MercadoLivreEventsContractTests.AssertPayload(
            Assert.Single(MercadoLivreEventsTestHandler.ItemChangesFor(notificationId)), ItemChangedV1);
        Assert.Equal(account.AccountId, payload.GetProperty("accountId").GetGuid());
        Assert.Equal(account.WorkspaceId, payload.GetProperty("workspaceId").GetGuid());
        Assert.Equal(account.Seller.Id, payload.GetProperty("mlUserId").GetInt64());
        Assert.Equal(itemId, payload.GetProperty("itemId").GetString());
        Assert.Equal(notificationId, payload.GetProperty("notificationId").GetString());
        Assert.Equal(
            MercadoLivreNotifications.ParseTimestamp(notification["sent"]!.GetValue<string>()),
            MercadoLivreEventsContractTests.OccurredAt(payload));
    }
}
