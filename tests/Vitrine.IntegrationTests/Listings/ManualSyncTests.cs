using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-08 — <c>POST /api/v1/listings/sync</c>: 202 e a sincronização roda; durante <c>running</c> não duplica;
/// menos de 5 min depois de uma manual concluída responde 429 <c>SYNC_TOO_SOON</c>; conta em reautorização responde 409;
/// sem antiforgery responde 400. O intervalo mínimo é medido no <see cref="TimeProvider"/> do host (relógio do teste).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ManualSyncTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-08")]
    public async Task Sync_responde_202_e_roda_nao_duplica_em_running_e_responde_429_antes_de_5_minutos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        var clock = new AdjustableClock();
        await using var factory = ListingsHost.Create(connectionString, gateway).WithClock(clock);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado uma conta importada (sincronização "connected", que não conta para o intervalo mínimo)
        var item = ListingItem.New("active", price: 30m);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, item);
        var imported = await ListingsDatabase.ReadSyncStateAsync(connectionString, account.AccountId, cancellationToken);
        var runsBefore = gateway.RunsStarted(account.AccountId);
        gateway.Put(account.AccountId, item with { Price = 31m, LastUpdated = item.LastUpdated.AddHours(1) });

        // Quando POST /listings/sync é pedido com a busca do ML segurada
        using (gateway.Hold())
        {
            var queued = await api.SyncAcceptedAsync(account.AccountId, cancellationToken);

            // Então 202 com a conta em queued, e a sincronização começa (running)
            Assert.Equal([account.AccountId], queued);
            await ListingsDatabase.WaitForSyncStateAsync(
                connectionString, account.AccountId, state => state.Status == "running", "a sincronização manual não ficou running", cancellationToken);

            // E um 2º pedido durante running responde 202 com a conta em queued, sem enfileirar outra
            Assert.Equal([account.AccountId], await api.SyncAcceptedAsync(account.AccountId, cancellationToken));
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var state = await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, imported!.CompletedAt, cancellationToken);
        Assert.Equal(31m, Assert.Single(await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken)).Price);
        Assert.NotNull(state.LastManualAt);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        Assert.Equal(runsBefore + 1, gateway.RunsStarted(account.AccountId));

        // E um pedido 4 min depois da manual concluída responde 429 SYNC_TOO_SOON com retryAfterSeconds
        clock.Advance(TimeSpan.FromMinutes(4));
        using (var tooSoon = await api.SyncAsync(account.AccountId, cancellationToken))
        {
            var problem = await IdentityApi.AssertProblemAsync(HttpStatusCode.TooManyRequests, "SYNC_TOO_SOON", tooSoon, cancellationToken);
            Assert.True(
                problem.TryGetProperty("retryAfterSeconds", out var retryAfter) && retryAfter.ValueKind == JsonValueKind.Number,
                $"429 SYNC_TOO_SOON sem retryAfterSeconds numérico: {problem}");
            Assert.InRange(retryAfter.GetInt32(), 1, 60);
        }

        Assert.Equal(runsBefore + 1, gateway.RunsStarted(account.AccountId));

        // E passados os 5 min, um novo pedido volta a ser aceito e roda
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.Equal([account.AccountId], await api.SyncAcceptedAsync(account.AccountId, cancellationToken));
        await ListingsDatabase.WaitForIdleAsync(connectionString, account.AccountId, state.CompletedAt, cancellationToken);
        Assert.Equal(runsBefore + 2, gateway.RunsStarted(account.AccountId));
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-08")]
    public async Task Sync_sem_accountId_enfileira_todas_as_contas_conectadas_do_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        using var stranger = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var other = await stranger.SignUpAsync(cancellationToken);

        // Dado duas contas no workspace e uma em outro workspace
        var first = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New());
        var second = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New());
        var foreign = await ListingsHost.ConnectAsync(factory, gateway, other.WorkspaceId, cancellationToken, ListingItem.New());
        var firstBefore = await ListingsDatabase.ReadSyncStateAsync(connectionString, first.AccountId, cancellationToken);
        var secondBefore = await ListingsDatabase.ReadSyncStateAsync(connectionString, second.AccountId, cancellationToken);
        var foreignRuns = gateway.RunsStarted(foreign.AccountId);

        // Quando POST /listings/sync é pedido com corpo {}
        var queued = await api.SyncAcceptedAsync(null, cancellationToken);

        // Então 202 com as duas contas do workspace, que sincronizam; a do outro workspace não
        Assert.Equal(new[] { first.AccountId, second.AccountId }.Order(), queued.Order());
        await ListingsDatabase.WaitForIdleAsync(connectionString, first.AccountId, firstBefore!.CompletedAt, cancellationToken);
        await ListingsDatabase.WaitForIdleAsync(connectionString, second.AccountId, secondBefore!.CompletedAt, cancellationToken);
        Assert.Equal(foreignRuns, gateway.RunsStarted(foreign.AccountId));
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-08")]
    public async Task Sync_de_conta_em_reautorizacao_responde_409_e_sem_antiforgery_responde_400()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New());
        var runsBefore = gateway.RunsStarted(account.AccountId);

        // Sem antiforgery (sem token e com token adulterado): 400 ANTIFORGERY_INVALID, e nada é enfileirado
        using (var withoutToken = await api.Http.PostWithTokenAsync(ListingsApi.SyncPath, new { accountId = account.AccountId }, null, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "ANTIFORGERY_INVALID", withoutToken, cancellationToken);
        }

        var token = await api.Http.GetAntiforgeryTokenAsync(cancellationToken);
        using (var tampered = await api.Http.PostWithTokenAsync(ListingsApi.SyncPath, new { accountId = account.AccountId }, token.Tampered(), cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "ANTIFORGERY_INVALID", tampered, cancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal(runsBefore, gateway.RunsStarted(account.AccountId));

        // Dado a conta em paused por reautorização; quando o sync dela é pedido: 409 ACCOUNT_REAUTHORIZATION_REQUIRED
        await ListingsHost.PublishAsync(factory, ListingsHost.ReauthorizationRequired(account));
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, account.AccountId, state => state.Status == "paused", "a conta não ficou paused", cancellationToken);
        using (var paused = await api.SyncAsync(account.AccountId, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Conflict, "ACCOUNT_REAUTHORIZATION_REQUIRED", paused, cancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal(runsBefore, gateway.RunsStarted(account.AccountId));
        Assert.Equal("paused", (await ListingsDatabase.ReadSyncStateAsync(connectionString, account.AccountId, cancellationToken))!.Status);
    }
}
