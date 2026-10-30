using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>Listar e desconectar contas, papéis, isolamento por workspace e antiforgery (contrato da SPEC-0020).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AccountsTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0020:IT-05")]
    public async Task Owner_desconecta_a_conta_os_tokens_somem_o_evento_vai_ao_outbox_e_a_lista_nao_a_mostra()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var seller = MlSeller.New();

        // Dado uma conta conectada (e listada)
        var account = await api.ConnectAsync(fake, seller, MlTokens.New(), cancellationToken);
        var listed = Assert.Single(await api.ListSuccessfullyAsync(cancellationToken));
        Assert.Equal(account.AccountId, listed.GetProperty("accountId").GetGuid());

        using (MercadoLivreEventsTestHandler.Hold())
        {
            // Quando o Owner chama DELETE
            using var response = await api.DisconnectAsync(account.AccountId, cancellationToken);

            // Então 204, e a linha da conta (com os tokens) não existe mais
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, response, cancellationToken);
            Assert.Equal(0, await MercadoLivreDatabase.CountAccountsAsync(connectionString, cancellationToken));

            // E MercadoLivreAccountDisconnected está no outbox junto com a remoção
            Assert.True(
                await MercadoLivreDatabase.CountEnvelopesAsync(connectionString, nameof(MercadoLivreAccountDisconnected), account.AccountId, cancellationToken) >= 1,
                "MercadoLivreAccountDisconnected da conta desconectada não está no armazenamento durável do Wolverine (outbox).");
        }

        // E GET /accounts não a lista mais
        Assert.Empty(await api.ListSuccessfullyAsync(cancellationToken));

        // E o assinante recebe a desconexão da conta, do workspace e do vendedor certos
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountDisconnected>(account.AccountId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreAccountDisconnected",
            cancellationToken);
        var disconnected = Assert.Single(MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountDisconnected>(account.AccountId));
        Assert.Equal(owner.WorkspaceId, disconnected.WorkspaceId);
        Assert.Equal(seller.Id, disconnected.MlUserId);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-05")]
    public async Task Lista_traz_os_campos_do_contrato_em_ordem_de_conexao_e_nunca_tokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);

        // Dado duas contas conectadas em sequência no mesmo workspace
        var firstSeller = MlSeller.New();
        var first = await api.ConnectAsync(fake, firstSeller, MlTokens.New(), cancellationToken);
        var secondSeller = MlSeller.New();
        var second = await api.ConnectAsync(fake, secondSeller, MlTokens.New(), cancellationToken);

        // Quando o Owner lista as contas
        var accounts = await api.ListSuccessfullyAsync(cancellationToken);

        // Então vêm as duas, ordenadas por connectedAt, exatamente com os campos do contrato (sem tokens)
        Assert.Equal([first.AccountId, second.AccountId], accounts.Select(account => account.GetProperty("accountId").GetGuid()));
        foreach (var (account, seller) in accounts.Zip([firstSeller, secondSeller]))
        {
            Assert.Equal(
                ["accountId", "connectedAt", "mlUserId", "nickname", "siteId", "status"],
                account.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
            Assert.Equal(JsonValueKind.Number, account.GetProperty("mlUserId").ValueKind);
            Assert.Equal(seller.Id, account.GetProperty("mlUserId").GetInt64());
            Assert.Equal(seller.Nickname, account.GetProperty("nickname").GetString());
            Assert.Equal("MLB", account.GetProperty("siteId").GetString());
            Assert.Equal("connected", account.GetProperty("status").GetString());
            Assert.True(account.GetProperty("connectedAt").TryGetDateTimeOffset(out _), $"connectedAt não é data-hora: {account}");
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-05")]
    public async Task DELETEs_simultaneos_da_mesma_conta_respondem_204_e_204_ou_404_nunca_500()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);

        // Algumas rodadas, cada uma com uma conta nova: a corrida aparece sem hooks no código de produção.
        for (var round = 0; round < 5; round++)
        {
            // Dado uma conta conectada
            var account = await api.ConnectAsync(fake, MlSeller.New(), MlTokens.New(), cancellationToken);

            // Quando dois DELETE da mesma conta chegam ao mesmo tempo
            var responses = await Task.WhenAll(
                api.DisconnectAsync(account.AccountId, cancellationToken),
                api.DisconnectAsync(account.AccountId, cancellationToken));
            try
            {
                // Então um responde 204 e o outro 204 ou 404 — nunca 500
                var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync(cancellationToken)));
                var detail = $"rodada {round}: {string.Join(" | ", responses.Zip(bodies, (response, body) => $"{(int)response.StatusCode} {body}"))}";
                var statuses = responses.Select(response => (int)response.StatusCode).Order().ToList();
                Assert.True(statuses.Contains(204), $"Nenhum DELETE respondeu 204: {detail}");
                Assert.True(statuses.All(status => status is 204 or 404), $"Status fora do aceitável: {detail}");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            // E a conta não existe mais
            Assert.Equal(0, await MercadoLivreDatabase.CountAccountsAsync(connectionString, cancellationToken));
        }
    }

    /// <summary>Operação de Owner tentada por um Member.</summary>
    public enum OwnerOperation
    {
        /// <summary><c>POST /connections</c>.</summary>
        Start,

        /// <summary><c>POST /connections/complete</c>.</summary>
        Complete,

        /// <summary><c>DELETE /accounts/{accountId}</c>.</summary>
        Disconnect,
    }

    [Theory]
    [Trait("spec", "SPEC-0020:IT-06")]
    [InlineData(OwnerOperation.Start)]
    [InlineData(OwnerOperation.Complete)]
    [InlineData(OwnerOperation.Disconnect)]
    public async Task Member_nao_conecta_nem_desconecta_403_FORBIDDEN_mas_lista_as_contas(OwnerOperation operation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado um workspace com uma conta conectada pelo Owner
        using var ownerApi = new MercadoLivreApi(factory);
        var owner = await ownerApi.SignUpAsync(cancellationToken);
        var account = await ownerApi.ConnectAsync(fake, MlSeller.New(), MlTokens.New(), cancellationToken);

        // E um Member desse workspace (criado direto no banco: não há convite nesta versão)
        using var memberApi = new MercadoLivreApi(factory);
        var member = await memberApi.SignUpAsync(cancellationToken);
        await MercadoLivreDatabase.MakeMemberAsync(connectionString, member.UserId, member.WorkspaceId, owner.WorkspaceId, cancellationToken);
        fake.ForgetCalls();

        // Quando o Member tenta a operação de Owner
        using var response = operation switch
        {
            OwnerOperation.Start => await memberApi.StartAsync(cancellationToken),
            OwnerOperation.Complete => await memberApi.CompleteAsync(MercadoLivreApi.NewCode(), (await ownerApi.StartSuccessfullyAsync(cancellationToken)).State, cancellationToken),
            OwnerOperation.Disconnect => await memberApi.DisconnectAsync(account.AccountId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        // Então 403 FORBIDDEN, sem chamar o ML e sem mexer na conta
        await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", response, cancellationToken);
        Assert.Empty(fake.CallsExceptListingsImport);
        Assert.Equal(account.AccountId, Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken)).Id);

        // E GET /accounts responde 200 ao Member, com a conta do workspace
        var listed = Assert.Single(await memberApi.ListSuccessfullyAsync(cancellationToken));
        Assert.Equal(account.AccountId, listed.GetProperty("accountId").GetGuid());
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-06")]
    public async Task Usuario_sem_workspace_recebe_403_FORBIDDEN_em_todos_os_endpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        using var api = new MercadoLivreApi(factory);

        // Dado um usuário logado que não pertence a nenhum workspace
        var user = await api.SignUpAsync(cancellationToken);
        await MercadoLivreDatabase.RemoveWorkspaceAsync(connectionString, user.UserId, user.WorkspaceId, cancellationToken);

        // Quando chama cada endpoint do módulo; então 403 FORBIDDEN
        using (var list = await api.ListAsync(cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", list, cancellationToken);
        }

        using (var start = await api.StartAsync(cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", start, cancellationToken);
        }

        using (var disconnect = await api.DisconnectAsync(Guid.NewGuid(), cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN", disconnect, cancellationToken);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-08")]
    public async Task Cada_workspace_so_ve_as_proprias_contas_e_DELETE_da_conta_alheia_responde_404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado dois workspaces com uma conta cada
        using var a = new MercadoLivreApi(factory);
        await a.SignUpAsync(cancellationToken);
        var accountA = await a.ConnectAsync(fake, MlSeller.New(), MlTokens.New(), cancellationToken);
        using var b = new MercadoLivreApi(factory);
        await b.SignUpAsync(cancellationToken);
        var accountB = await b.ConnectAsync(fake, MlSeller.New(), MlTokens.New(), cancellationToken);

        // Quando cada Owner lista; então só vê a própria conta
        Assert.Equal([accountA.AccountId], (await a.ListSuccessfullyAsync(cancellationToken)).Select(account => account.GetProperty("accountId").GetGuid()));
        Assert.Equal([accountB.AccountId], (await b.ListSuccessfullyAsync(cancellationToken)).Select(account => account.GetProperty("accountId").GetGuid()));

        // Quando cada Owner tenta apagar a conta do outro (e uma inexistente); então 404 NOT_FOUND, sem revelar existência
        foreach (var (api, target) in new[] { (a, accountB.AccountId), (b, accountA.AccountId), (a, Guid.NewGuid()) })
        {
            using var response = await api.DisconnectAsync(target, cancellationToken);
            await IdentityApi.AssertProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND", response, cancellationToken);
        }

        // E as duas contas continuam lá
        Assert.Equal(2, await MercadoLivreDatabase.CountAccountsAsync(connectionString, cancellationToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-09")]
    public async Task POST_e_DELETE_sem_token_antiforgery_respondem_400_e_nao_alteram_nada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);
        var account = await api.ConnectAsync(fake, MlSeller.New(), MlTokens.New(), cancellationToken);
        var started = await api.StartSuccessfullyAsync(cancellationToken);
        var pendingBefore = (await MercadoLivreDatabase.ReadPendingAuthorizationsAsync(connectionString, cancellationToken)).Count;
        fake.ForgetCalls();

        // Dado o cookie antiforgery no navegador, mas sem o cabeçalho com o token; quando cada POST/DELETE é enviado
        _ = await api.Http.GetAntiforgeryTokenAsync(cancellationToken);
        using (var start = await api.Http.PostWithTokenAsync(MercadoLivreApi.ConnectionsPath, new { }, token: null, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "ANTIFORGERY_INVALID", start, cancellationToken);
        }

        using (var complete = await api.Http.PostWithTokenAsync(
            MercadoLivreApi.CompletePath, new { code = MercadoLivreApi.NewCode(), state = started.State }, token: null, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "ANTIFORGERY_INVALID", complete, cancellationToken);
        }

        using (var disconnect = await api.Http.DeleteWithTokenAsync(MercadoLivreApi.AccountPath(account.AccountId), token: null, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "ANTIFORGERY_INVALID", disconnect, cancellationToken);
        }

        // Então nada mudou: nenhuma autorização nova, o ML não foi chamado e a conta continua conectada
        Assert.Equal(pendingBefore, (await MercadoLivreDatabase.ReadPendingAuthorizationsAsync(connectionString, cancellationToken)).Count);
        Assert.Empty(fake.CallsExceptListingsImport);
        Assert.Equal(account.AccountId, Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken)).Id);
    }
}
