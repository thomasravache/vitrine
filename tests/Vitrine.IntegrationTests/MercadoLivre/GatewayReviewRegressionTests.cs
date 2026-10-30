using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Infrastructure;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Regressões apontadas no review (G4) da SPEC-0021: refresh lento nunca retentado, 401 com token já substituído, log
/// Warning ao exigir reconexão pelo 401 e item malformado no <c>/items/bulk</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewayReviewRegressionTests(PostgresFixture postgres)
{
    private const string TokenRefreshes = "vitrine.mercadolivre.token_refreshes";

    [Fact]
    [Trait("spec", "SPEC-0021:IT-02")]
    public async Task Refresh_mais_lento_que_o_timeout_da_tentativa_nao_e_retentado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o relógio acelerado (os 10 s da tentativa viram 0,5 s) e uma conta com token expirando
        var clock = new AcceleratedTimeProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithClock(clock)
            .WithFastResilience();
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var metrics = new AuthMetricsListener(factory.Services.GetRequiredService<IMeterFactory>(), MercadoLivreModule.MeterName);
        var account = await MercadoLivreGateway.SeedAccountAsync(
            factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken);

        // E o POST /oauth/token demorando 3 s reais (60 s no relógio acelerado: passa do timeout da tentativa)
        var renewed = MlTokens.New();
        fake.TokenRespondsSlowly(renewed, account.Seller.Id, TimeSpan.FromSeconds(3));
        fake.BulkResponds(requiredToken: renewed.AccessToken);

        // Quando há uma chamada; então ela termina em Unavailable
        var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);

        // E houve exatamente 1 POST /oauth/token (contado na chegada): o refresh de uso único nunca é retentado
        Assert.Equal(1, fake.TokenArrivals);

        // E a conta não foi para reauthorization_required: segue conectada, com os tokens antigos ou os renovados
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(MercadoLivreAccountStatus.Connected, stored.Status);
        Assert.Contains(stored.AccessToken, new[] { account.Tokens.AccessToken, renewed.AccessToken });

        // E, se a conta não foi renovada, a falha do refresh foi medida como unavailable
        if (stored.AccessToken == account.Tokens.AccessToken)
        {
            Assert.True(metrics.Total(TokenRefreshes, "unavailable") == 1, $"Esperado {TokenRefreshes}{{unavailable}} = 1. {metrics.Describe()}");
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-05")]
    public async Task Segundo_401_com_token_ja_substituido_por_outra_renovacao_lanca_Unavailable_e_a_conta_segue_conectada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado uma conta com T1; o ML rejeita T1 (401), a renovação forçada emite T2 e o ML rejeita T2 também
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var t2 = MlTokens.New();
        var t3 = MlTokens.New();
        fake.TokenResponds(t2, account.Seller.Id);

        // E, enquanto o 2º 401 (com T2) está a caminho, outra renovação troca a conta para T3
        fake.BulkAlwaysRejects(authorization =>
        {
            if (authorization == $"Bearer {t2.AccessToken}")
            {
                MercadoLivreGateway.ReplaceTokensAsync(factory, account.AccountId, t3, CancellationToken.None).GetAwaiter().GetResult();
            }
        });

        // Quando a chamada recebe o 2º 401; então lança Unavailable (T2 já não é o token da conta), não ReauthorizationRequired
        var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);

        // E a conta continua connected, com T3
        var row = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
        Assert.Equal("connected", row.Status);
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(t3.AccessToken, stored.AccessToken);
        Assert.Equal(t3.RefreshToken, stored.RefreshToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-05")]
    public async Task Conta_que_vai_para_reauthorization_required_pelo_segundo_401_gera_log_Warning_com_accountId_e_sem_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithLogs(logs);
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado o bulk respondendo 401 sempre, mesmo com o token renovado
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var renewed = MlTokens.New();
        fake.TokenResponds(renewed, account.Seller.Id);
        fake.BulkAlwaysFails(401);

        // Quando a conta vai para reauthorization_required pelo caminho do 2º 401
        await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));

        // Então sai um log Warning com o accountId
        var accountId = account.AccountId.ToString("D");
        var warnings = logs.Records.Where(record => record.Level == LogLevel.Warning).ToList();
        Assert.True(
            warnings.Any(record => record.Message.Contains(accountId, StringComparison.OrdinalIgnoreCase)),
            $"Nenhum log Warning com o accountId {accountId}. Warnings:\n" + string.Join('\n', warnings));

        // E nenhum log traz token (antigo ou renovado) nem o client secret
        foreach (var record in logs.Records)
        {
            var text = record.ToString();
            foreach (var secret in new[]
            {
                account.Tokens.AccessToken, account.Tokens.RefreshToken, renewed.AccessToken, renewed.RefreshToken, MercadoLivreHost.ClientSecret,
            })
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"Segredo apareceu em log:\n{text}");
            }
        }
    }

    [Theory]
    [Trait("spec", "SPEC-0021:IT-07")]
    [InlineData("available_quantity")]
    [InlineData("last_updated")]
    public async Task Item_200_com_body_malformado_vira_Unavailable_so_para_esse_id(string missingField)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado um lote em que um item vem com status_code 200 e body sem um campo do contrato
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var ids = MlItem.NewIds(3);
        fake.BulkResponds(
            id => id == ids[1] ? MlItem.Found(id, body => body.Remove(missingField)) : MlItem.Found(id),
            account.Tokens.AccessToken);

        // Quando GetItemsAsync é chamado; então não lança
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, ids, cancellationToken);

        // E só o item malformado vira Unavailable; os demais voltam Found, na ordem pedida
        Assert.Equal(ids, results.Select(result => result.ItemId));
        Assert.Equal(ids[0], Assert.IsType<MercadoLivreItemResult.Found>(results[0]).Item.Id);
        Assert.Equal(ids[1], Assert.IsType<MercadoLivreItemResult.Unavailable>(results[1]).ItemId);
        Assert.Equal(ids[2], Assert.IsType<MercadoLivreItemResult.Found>(results[2]).Item.Id);
    }
}
