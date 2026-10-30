using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.IntegrationEvents;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Gateway autenticado (SPEC-0021): Bearer da conta, renovação antecipada sob lock com refresh token de uso único,
/// "reconectar necessário" quando o ML recusa o refresh e renovação forçada em 401.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewayAuthenticationTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0021:IT-01")]
    public async Task Conta_com_token_valido_chama_o_ML_com_Bearer_sem_renovar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com token válido por mais 6 h
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.BulkResponds(requiredToken: account.Tokens.AccessToken);
        var itemId = Assert.Single(MlItem.NewIds(1));

        // Quando GetItemsAsync é chamado
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, [itemId], cancellationToken);

        // Então o ML recebe Authorization: Bearer <token> e nenhum POST /oauth/token
        var bulk = Assert.Single(fake.BulkCalls);
        Assert.Equal($"Bearer {account.Tokens.AccessToken}", MercadoLivreFake.AuthorizationOf(bulk));
        Assert.Empty(fake.TokenCalls);
        var found = Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        Assert.Equal(itemId, found.Item.Id);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-01")]
    public async Task Conta_inexistente_lanca_AccountNotFound_sem_chamar_o_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);
        fake.BulkResponds();
        var missing = Guid.NewGuid();

        // Quando os dois métodos são chamados para uma conta que não existe
        var onGet = await Assert.ThrowsAsync<MercadoLivreAccountNotFoundException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, missing, MlItem.NewIds(1), cancellationToken));
        var onSearch = await Assert.ThrowsAsync<MercadoLivreAccountNotFoundException>(
            () => MercadoLivreGateway.SearchItemIdsAsync(factory, missing, "active", cancellationToken));

        // Então lançam AccountNotFound com a conta pedida, sem nenhuma chamada ao ML
        Assert.Equal(missing, onGet.AccountId);
        Assert.Equal(missing, onSearch.AccountId);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-01")]
    public async Task Conta_em_reauthorization_required_lanca_ReauthorizationRequired_sem_chamar_o_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        fake.BulkResponds();
        fake.TokenResponds(MlTokens.New(), 1);

        // Dado uma conta já em reauthorization_required (token ainda expirando, para tentar uma renovação)
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
        await MercadoLivreDatabase.SetStatusAsync(connectionString, account.AccountId, "reauthorization_required", cancellationToken);

        // Quando os dois métodos são chamados
        var onGet = await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        var onSearch = await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.SearchItemIdsAsync(factory, account.AccountId, "active", cancellationToken));

        // Então lançam ReauthorizationRequired, sem chamar o ML (nem /oauth/token)
        Assert.Equal(account.AccountId, onGet.AccountId);
        Assert.Equal(account.AccountId, onSearch.AccountId);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-02")]
    public async Task Token_expirando_renova_uma_vez_grava_os_tokens_novos_cifrados_e_segue_com_o_token_novo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com token expirando em 2 min e o ML emitindo tokens novos no refresh
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken);
        var renewed = MlTokens.New();
        fake.TokenResponds(renewed, account.Seller.Id);
        fake.BulkResponds(requiredToken: renewed.AccessToken);

        // Quando há uma chamada
        var before = DateTimeOffset.UtcNow;
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);
        var after = DateTimeOffset.UtcNow;

        // Então houve exatamente 1 POST /oauth/token com grant_type=refresh_token e o refresh token antigo
        Assert.Single(fake.TokenCalls);
        var form = fake.LastTokenForm();
        Assert.Equal("refresh_token", form.GetValueOrDefault("grant_type"));
        Assert.Equal(account.Tokens.RefreshToken, form.GetValueOrDefault("refresh_token"));
        Assert.Equal(MercadoLivreHost.ClientId, form.GetValueOrDefault("client_id"));
        Assert.Equal(MercadoLivreHost.ClientSecret, form.GetValueOrDefault("client_secret"));

        // E a chamada seguiu com o token novo
        Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        Assert.Equal($"Bearer {renewed.AccessToken}", MercadoLivreFake.AuthorizationOf(Assert.Single(fake.BulkCalls)));

        // E os tokens novos estão gravados (o refresh token novo substitui o antigo), com a validade nova
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(MercadoLivreAccountStatus.Connected, stored.Status);
        Assert.Equal(renewed.AccessToken, stored.AccessToken);
        Assert.Equal(renewed.RefreshToken, stored.RefreshToken);
        Assert.InRange(
            stored.AccessTokenExpiresAt,
            before.AddSeconds(renewed.ExpiresIn).AddSeconds(-5),
            after.AddSeconds(renewed.ExpiresIn).AddSeconds(5));

        // E cifrados: nenhum token (novo ou antigo) aparece em claro na linha
        var dump = await MercadoLivreDatabase.DumpAsync(connectionString, "accounts", cancellationToken);
        foreach (var token in new[] { renewed.AccessToken, renewed.RefreshToken, account.Tokens.AccessToken, account.Tokens.RefreshToken })
        {
            Assert.DoesNotContain(token, dump, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-02")]
    public async Task Refresh_recusado_com_invalid_grant_leva_a_conta_a_reauthorization_required_com_evento_no_outbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com token expirando e o ML recusando o refresh (invalid_grant)
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken);
        fake.TokenFails(400, "invalid_grant");
        fake.BulkResponds();

        using (MercadoLivreEventsTestHandler.Hold())
        {
            // Quando há uma chamada; então ReauthorizationRequired é lançada
            var thrown = await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
                () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
            Assert.Equal(account.AccountId, thrown.AccountId);

            // E MercadoLivreAccountReauthorizationRequired da conta está no outbox
            Assert.True(
                await MercadoLivreDatabase.CountEnvelopesAsync(
                    connectionString, nameof(MercadoLivreAccountReauthorizationRequired), account.AccountId, cancellationToken) >= 1,
                "MercadoLivreAccountReauthorizationRequired não está no armazenamento durável do Wolverine (outbox).");
        }

        // E a conta fica reauthorization_required, com os tokens apagados, sem chamar a API com o token antigo
        var row = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
        Assert.Equal("reauthorization_required", row.Status);
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.True(string.IsNullOrEmpty(stored.AccessToken), "O access token não foi apagado.");
        Assert.True(string.IsNullOrEmpty(stored.RefreshToken), "O refresh token não foi apagado.");
        Assert.Single(fake.TokenCalls);
        Assert.Empty(fake.BulkCalls);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-02")]
    public async Task Refresh_com_500_nao_muda_a_conta_lanca_Unavailable_e_nao_e_retentado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com token expirando e o /oauth/token respondendo 500
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken);
        fake.TokenFails(500, "internal_error");
        fake.BulkResponds();

        // Quando há uma chamada; então Unavailable é lançada
        var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);

        // E o ML recebeu exatamente 1 POST /oauth/token (o refresh nunca é retentado)
        Assert.Single(fake.TokenCalls);
        Assert.Empty(fake.BulkCalls);

        // E a conta não mudou: conectada, com os mesmos tokens e a mesma validade
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(MercadoLivreAccountStatus.Connected, stored.Status);
        Assert.Equal(account.Tokens.AccessToken, stored.AccessToken);
        Assert.Equal(account.Tokens.RefreshToken, stored.RefreshToken);
        Assert.Equal(account.ExpiresAt, stored.AccessTokenExpiresAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-03")]
    public async Task Cinco_chamadas_paralelas_com_token_expirado_fazem_um_unico_refresh_e_usam_o_token_novo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com token expirado e um refresh lento (amplia a janela de corrida)
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddHours(-1), cancellationToken);
        var renewed = MlTokens.New();
        fake.TokenResponds(renewed, account.Seller.Id, delay: TimeSpan.FromMilliseconds(500));
        fake.BulkResponds(requiredToken: renewed.AccessToken);

        // Quando 5 chamadas GetItemsAsync rodam em paralelo, cada uma no seu escopo de DI
        using var start = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(
                async () =>
                {
                    start.Wait(cancellationToken);
                    return await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);
                },
                cancellationToken))
            .ToList();
        start.Set();
        var results = await Task.WhenAll(calls);

        // Então o ML registrou exatamente 1 POST /oauth/token
        Assert.Single(fake.TokenCalls);

        // E as 5 chamadas ao bulk usaram o token novo
        Assert.All(results, result => Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(result)));
        var bulk = fake.BulkCalls;
        Assert.Equal(5, bulk.Count);
        Assert.All(bulk, entry => Assert.Equal($"Bearer {renewed.AccessToken}", MercadoLivreFake.AuthorizationOf(entry)));
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-05")]
    public async Task Um_401_na_API_forca_uma_renovacao_e_a_chamada_repetida_conclui()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com token válido pela data, mas que o ML rejeita (401); o refresh emite um token novo
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var renewed = MlTokens.New();
        fake.TokenResponds(renewed, account.Seller.Id);
        fake.BulkResponds(requiredToken: renewed.AccessToken);

        // Quando GetItemsAsync é chamado
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);

        // Então houve 1 renovação forçada e a chamada repetida, com o token novo, concluiu
        Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        Assert.Single(fake.TokenCalls);
        Assert.Equal(account.Tokens.RefreshToken, fake.LastTokenForm().GetValueOrDefault("refresh_token"));
        var bulk = fake.BulkCalls;
        Assert.Equal(2, bulk.Count);
        Assert.Equal($"Bearer {account.Tokens.AccessToken}", MercadoLivreFake.AuthorizationOf(bulk[0]));
        Assert.Equal($"Bearer {renewed.AccessToken}", MercadoLivreFake.AuthorizationOf(bulk[1]));

        // E a conta segue conectada com os tokens novos
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(MercadoLivreAccountStatus.Connected, stored.Status);
        Assert.Equal(renewed.AccessToken, stored.AccessToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-05")]
    public async Task Novo_401_depois_da_renovacao_lanca_ReauthorizationRequired_e_a_conta_fica_nesse_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado o bulk respondendo 401 sempre, mesmo com o token renovado
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.TokenResponds(MlTokens.New(), account.Seller.Id);
        fake.BulkAlwaysFails(401);

        // Quando GetItemsAsync é chamado; então lança ReauthorizationRequired depois de 1 renovação
        var thrown = await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);
        Assert.Single(fake.TokenCalls);
        Assert.Equal(2, fake.BulkCalls.Count);

        // E a conta fica reauthorization_required
        var row = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
        Assert.Equal("reauthorization_required", row.Status);
    }
}
