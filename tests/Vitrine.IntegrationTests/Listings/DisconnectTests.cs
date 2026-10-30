using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>SPEC-0022:IT-06 — <c>MercadoLivreAccountDisconnected</c> apaga os anúncios e o estado da conta (retenção).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DisconnectTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-06")]
    public async Task Conta_desconectada_nao_deixa_linhas_em_listings_nem_em_sync_states_e_as_outras_ficam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado duas contas importadas no mesmo workspace
        var account = await ListingsHost.ConnectAsync(
            factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New("active"), ListingItem.New("paused"), ListingItem.New("closed"));
        var other = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New("active"));
        Assert.Equal((3L, 1L), await ListingsDatabase.CountRowsAsync(connectionString, account.AccountId, cancellationToken));

        // Quando MercadoLivreAccountDisconnected da primeira é processado
        await ListingsHost.PublishAsync(factory, ListingsHost.Disconnected(account));

        // Então não restam linhas dela em listings nem em sync_states
        await Eventually.TrueAsync(
            async () => await ListingsDatabase.CountRowsAsync(connectionString, account.AccountId, cancellationToken) == (0L, 0L),
            TimeSpan.FromSeconds(30),
            "anúncios ou sync_states da conta desconectada continuam no banco",
            cancellationToken);

        // E a outra conta continua intacta, inclusive para a listagem
        Assert.Equal((1L, 1L), await ListingsDatabase.CountRowsAsync(connectionString, other.AccountId, cancellationToken));
        var (listed, _) = await api.ListAllAsync("status=all", cancellationToken);
        Assert.All(listed, item => Assert.Equal(other.AccountId, item.GetProperty("accountId").GetGuid()));
        Assert.Single(listed);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-06")]
    public async Task Notificacao_em_curso_quando_a_conta_e_desconectada_nao_deixa_linha_orfa()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var item = ListingItem.New("active", price: 10m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, item);
        gateway.Put(account.AccountId, item with { Price = 11m, LastUpdated = item.LastUpdated.AddHours(1) });

        // Dado uma notificação do anúncio segurada na leitura do ML (já passou pela checagem da conta)
        using (var read = gateway.HoldNextRead())
        {
            await ListingsHost.PublishAsync(factory, ListingsHost.ItemChanged(account, item.Id));
            Assert.Contains(item.Id, await read.Reached.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

            // Quando a conta é desconectada antes de a notificação gravar
            await ListingsHost.PublishAsync(factory, ListingsHost.Disconnected(account));
            await Eventually.TrueAsync(
                async () => await ListingsDatabase.CountRowsAsync(connectionString, account.AccountId, cancellationToken) == (0L, 0L),
                TimeSpan.FromSeconds(30),
                "a desconexão não apagou anúncios e estado da conta",
                cancellationToken);
        }

        // Então, solta a notificação, não sobra linha da conta, nem na listagem
        await Eventually.TrueAsync(
            () => gateway.Reads(account.AccountId, item.Id) > 1,
            TimeSpan.FromSeconds(30),
            "a notificação não chegou a reler o anúncio",
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        Assert.Equal((0L, 0L), await ListingsDatabase.CountRowsAsync(connectionString, account.AccountId, cancellationToken));
        var listed = await api.ListSuccessfullyAsync("status=all", cancellationToken);
        Assert.Empty(listed.Items);
    }
}
