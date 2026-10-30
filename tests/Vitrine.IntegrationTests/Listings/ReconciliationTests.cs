using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.Listings.Application.Sync;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-05 — reconciliação (ADR-0010) pelo estado persistido: <c>ReconcileListings</c> ressincroniza as contas
/// não <c>paused</c> cuja última sincronização (<c>coalesce(completed_at, started_at)</c>) é mais antiga que
/// <c>Listings:ReconciliationInterval</c> e corrige a alteração cuja notificação se perdeu; conta sincronizada há pouco
/// não é ressincronizada. Um timer curto publica <c>ReconcileListings</c>: a conta vencida é ressincronizada logo depois
/// de o host subir, sem esperar um intervalo inteiro de tempo de atividade do processo.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReconciliationTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-05")]
    public async Task ReconcileListings_ressincroniza_so_as_contas_nao_paused_com_sincronizacao_vencida()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado (intervalo padrão de 24 h) uma conta sincronizada há 25 h, uma sincronizada agora e uma paused há 25 h,
        // cada uma com um anúncio alterado só no dublê (notificação perdida)
        var staleItem = ListingItem.New("active", price: 50m);
        var stale = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, staleItem);
        var recentItem = ListingItem.New("active", price: 60m);
        var recent = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, recentItem);
        var pausedItem = ListingItem.New("active", price: 70m);
        var paused = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, pausedItem);
        await ListingsHost.PublishAsync(factory, ListingsHost.ReauthorizationRequired(paused));
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, paused.AccountId, state => state.Status == "paused", "a conta não ficou paused", cancellationToken);

        var dayAgo = DateTimeOffset.UtcNow.AddHours(-25);
        await ListingsDatabase.SetSyncStateAsync(connectionString, stale.AccountId, "idle", dayAgo.AddMinutes(-1), dayAgo, cancellationToken);
        await ListingsDatabase.SetSyncStateAsync(connectionString, paused.AccountId, "paused", dayAgo.AddMinutes(-1), dayAgo, cancellationToken);
        var recentRuns = gateway.RunsStarted(recent.AccountId);
        var pausedRuns = gateway.RunsStarted(paused.AccountId);
        gateway.Put(stale.AccountId, staleItem with { Price = 55m, LastUpdated = staleItem.LastUpdated.AddHours(1) });
        gateway.Put(recent.AccountId, recentItem with { Price = 66m, LastUpdated = recentItem.LastUpdated.AddHours(1) });
        gateway.Put(paused.AccountId, pausedItem with { Price = 77m, LastUpdated = pausedItem.LastUpdated.AddHours(1) });

        // Quando ReconcileListings é processado
        await ListingsHost.PublishAsync(factory, new ReconcileListings());

        // Então a conta vencida é ressincronizada e o anúncio fica atualizado
        await ListingsDatabase.WaitForIdleAsync(connectionString, stale.AccountId, dayAgo, cancellationToken);
        Assert.Equal(55m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, stale.AccountId, cancellationToken)).Price);

        // E a sincronizada agora e a paused não são enfileiradas: nenhuma busca nova, anúncios e estado intocados
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        Assert.Equal(recentRuns, gateway.RunsStarted(recent.AccountId));
        Assert.Equal(60m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, recent.AccountId, cancellationToken)).Price);
        Assert.Equal(pausedRuns, gateway.RunsStarted(paused.AccountId));
        Assert.Equal(70m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, paused.AccountId, cancellationToken)).Price);
        Assert.Equal("paused", (await ListingsDatabase.ReadSyncStateAsync(connectionString, paused.AccountId, cancellationToken))!.Status);
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-05")]
    public async Task Conta_vencida_e_ressincronizada_logo_depois_de_o_host_subir_e_a_recente_nao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        var staleItem = ListingItem.New("active", price: 10m);
        var recentItem = ListingItem.New("active", price: 20m);
        ListingsAccount stale;
        ListingsAccount recent;
        string masterKey;

        // Dado (intervalo de 24 h) duas contas importadas por um host que depois para
        await using (var first = ListingsHost.Create(connectionString, gateway).WithSetting("Listings:ReconciliationInterval", "1.00:00:00"))
        {
            masterKey = first.MasterKey;
            using var api = new ListingsApi(first);
            var owner = await api.SignUpAsync(cancellationToken);
            stale = await ListingsHost.ConnectAsync(first, gateway, owner.WorkspaceId, cancellationToken, staleItem);
            recent = await ListingsHost.ConnectAsync(first, gateway, owner.WorkspaceId, cancellationToken, recentItem);
        }

        // E, com o host parado, a última sincronização de uma delas é de 25 h atrás; as duas têm alteração só no dublê
        var dayAgo = DateTimeOffset.UtcNow.AddHours(-25);
        await ListingsDatabase.SetSyncStateAsync(connectionString, stale.AccountId, "idle", dayAgo.AddMinutes(-1), dayAgo, cancellationToken);
        var recentRuns = gateway.RunsStarted(recent.AccountId);
        gateway.Put(stale.AccountId, staleItem with { Price = 11m, LastUpdated = staleItem.LastUpdated.AddHours(1) });
        gateway.Put(recent.AccountId, recentItem with { Price = 22m, LastUpdated = recentItem.LastUpdated.AddHours(1) });

        // Quando um host novo sobe (mesmo intervalo de 24 h), sem nenhuma mensagem do teste
        await using var second = ListingsHost.Create(connectionString, gateway)
            .WithMasterKey(masterKey)
            .WithSetting("Listings:ReconciliationInterval", "1.00:00:00");
        _ = second.Services;

        // Então a conta vencida é ressincronizada em segundos, não depois de 24 h de tempo de atividade
        await ListingsDatabase.WaitForIdleAsync(connectionString, stale.AccountId, dayAgo, cancellationToken, TimeSpan.FromSeconds(30));
        Assert.Equal(11m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, stale.AccountId, cancellationToken)).Price);

        // E a recente não é ressincronizada
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        Assert.Equal(recentRuns, gateway.RunsStarted(recent.AccountId));
        Assert.Equal(20m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, recent.AccountId, cancellationToken)).Price);
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
