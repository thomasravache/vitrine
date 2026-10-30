using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.Listings.Application.Sync;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-05 — reconciliação (ADR-0010): <c>ReconcileListings</c> ressincroniza toda conta não <c>paused</c> e
/// corrige a alteração cuja notificação se perdeu; o agendador a publica a cada <c>Listings:ReconciliationInterval</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReconciliationTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-05")]
    public async Task ReconcileListings_ressincroniza_as_contas_nao_paused_e_corrige_a_alteracao_perdida()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta importada e uma conta paused, cada uma com um anúncio alterado só no dublê (notificação perdida)
        var item = ListingItem.New("active", price: 50m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, item);
        var pausedItem = ListingItem.New("active", price: 70m);
        var paused = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, pausedItem);
        await ListingsHost.PublishAsync(factory, ListingsHost.ReauthorizationRequired(paused));
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, paused.AccountId, state => state.Status == "paused", "a conta não ficou paused", cancellationToken);
        var before = await ListingsDatabase.ReadSyncStateAsync(connectionString, account.AccountId, cancellationToken);
        var pausedRunsBefore = gateway.RunsStarted(paused.AccountId);
        gateway.Put(account.AccountId, item with { Price = 55m, LastUpdated = item.LastUpdated.AddHours(1) });
        gateway.Put(paused.AccountId, pausedItem with { Price = 77m, LastUpdated = pausedItem.LastUpdated.AddHours(1) });

        // Quando ReconcileListings é processado
        await ListingsHost.PublishAsync(factory, new ReconcileListings());

        // Então a conta é ressincronizada e o anúncio fica atualizado
        await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, before!.CompletedAt, cancellationToken);
        var row = Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken));
        Assert.Equal(55m, row.Price);

        // E a conta paused não é enfileirada: nenhuma busca nova, anúncio e estado intocados
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        Assert.Equal(pausedRunsBefore, gateway.RunsStarted(paused.AccountId));
        Assert.Equal(70m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, paused.AccountId, cancellationToken)).Price);
        Assert.Equal("paused", (await ListingsDatabase.ReadSyncStateAsync(connectionString, paused.AccountId, cancellationToken))!.Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-05")]
    public async Task Com_o_intervalo_configurado_vencido_a_reconciliacao_roda_sozinha()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();

        // Dado o host com Listings:ReconciliationInterval de 3 segundos
        await using var factory = ListingsHost.Create(connectionString, gateway)
            .WithSetting("Listings:ReconciliationInterval", "00:00:03");
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // E uma conta importada com um anúncio alterado só no dublê
        var item = ListingItem.New("active", price: 10m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, item);
        gateway.Put(account.AccountId, item with { Price = 12m, LastUpdated = item.LastUpdated.AddHours(1) });

        // Quando o intervalo passa, sem nenhuma mensagem do teste
        // Então a conta é ressincronizada pela reconciliação e o anúncio fica atualizado
        await Eventually.TrueAsync(
            async () => (await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken)).Single().Price == 12m,
            TimeSpan.FromSeconds(30),
            "a reconciliação agendada não ressincronizou a conta dentro de 30 s com intervalo de 3 s",
            cancellationToken);
    }
}
