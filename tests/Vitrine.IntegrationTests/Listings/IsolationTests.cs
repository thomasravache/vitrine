using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-07 — isolamento por workspace: o usuário de B não vê, não filtra e não sincroniza os anúncios de A
/// (lista vazia no filtro e 404 no sync); usuário sem workspace recebe 403.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class IsolationTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-07")]
    public async Task Usuario_de_B_so_ve_os_proprios_anuncios_lista_vazia_no_filtro_de_A_e_404_no_sync_de_A()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var userA = new ListingsApi(factory);
        using var userB = new ListingsApi(factory);
        var ownerA = await userA.SignUpAsync(cancellationToken);
        var ownerB = await userB.SignUpAsync(cancellationToken);

        // Dado os workspaces A e B com anúncios
        var accountA = await ListingsHost.ConnectAsync(factory, gateway, ownerA.WorkspaceId, cancellationToken, ListingItem.New(), ListingItem.New());
        var accountB = await ListingsHost.ConnectAsync(factory, gateway, ownerB.WorkspaceId, cancellationToken, ListingItem.New("paused"));
        var runsOfA = gateway.RunsStarted(accountA.AccountId);

        // Quando o usuário de B lista: só os próprios anúncios
        var (listed, _) = await userB.ListAllAsync("status=all", cancellationToken);
        Assert.Equal([accountB.AccountId], listed.Select(item => item.GetProperty("accountId").GetGuid()));

        // E filtra pela conta de A: lista vazia
        var filtered = await userB.ListSuccessfullyAsync($"status=all&accountId={accountA.AccountId:D}", cancellationToken);
        Assert.Empty(filtered.Items);
        Assert.Null(filtered.NextCursor);

        // E o resumo de B só tem a conta de B
        var summary = await userB.SummarySuccessfullyAsync(cancellationToken);
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.Equal([accountB.AccountId], summary.GetProperty("accounts").EnumerateArray().Select(account => account.GetProperty("accountId").GetGuid()));

        // E pede sync da conta de A: 404 NOT_FOUND, sem sincronizar nada
        using (var sync = await userB.SyncAsync(accountA.AccountId, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND", sync, cancellationToken);
        }

        // E uma conta inexistente também é 404
        using (var unknown = await userB.SyncAsync(Guid.NewGuid(), cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND", unknown, cancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal(runsOfA, gateway.RunsStarted(accountA.AccountId));

        // E o usuário de A continua vendo só os seus
        var (listedA, _) = await userA.ListAllAsync("status=all", cancellationToken);
        Assert.Equal(2, listedA.Count);
        Assert.All(listedA, item => Assert.Equal(accountA.AccountId, item.GetProperty("accountId").GetGuid()));
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-07")]
    public async Task Sem_sessao_responde_401_e_usuario_sem_workspace_responde_403_FORBIDDEN()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = ListingsHost.Create(connectionString, new MercadoLivreItemsDouble());

        // Sem sessão: 401 nos três endpoints
        using (var anonymous = new ListingsApi(factory))
        {
            using var list = await anonymous.ListAsync(string.Empty, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
            using var summary = await anonymous.SummaryAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, summary.StatusCode);
            using var sync = await anonymous.SyncAsync(null, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, sync.StatusCode);
        }

        // Usuário sem workspace: 403 FORBIDDEN
        using var orphan = new ListingsApi(factory);
        var account = await orphan.SignUpAsync(cancellationToken);
        await MercadoLivre.Support.MercadoLivreDatabase.RemoveWorkspaceAsync(connectionString, account.UserId, account.WorkspaceId, cancellationToken);
        using (var list = await orphan.ListAsync(string.Empty, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", list, cancellationToken);
        }

        using (var summary = await orphan.SummaryAsync(cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", summary, cancellationToken);
        }

        using (var sync = await orphan.SyncAsync(null, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", sync, cancellationToken);
        }
    }
}
