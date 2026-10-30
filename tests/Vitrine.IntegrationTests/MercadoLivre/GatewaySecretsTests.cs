using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>Tokens e client secret nunca em log nem em mensagem de exceção do gateway (SPEC-0021, ADR-0006).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewaySecretsTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0021:IT-08")]
    public async Task Fluxos_de_renovacao_e_401_nao_deixam_token_refresh_token_nem_client_secret_em_logs_ou_excecoes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithLogs(logs);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var secrets = new List<(string Name, string Value)> { ("client secret", MercadoLivreHost.ClientSecret) };
        var exceptions = new List<Exception>();
        var expiring = DateTimeOffset.UtcNow.AddMinutes(2);

        // Dado os fluxos de IT-02: refresh 200, invalid_grant e 500
        var renewedAccount = await Seed(expiring);
        var renewed = Remember(MlTokens.New());
        fake.Reset();
        fake.TokenResponds(renewed, renewedAccount.Seller.Id);
        fake.BulkResponds(requiredToken: renewed.AccessToken);
        await MercadoLivreGateway.GetItemsAsync(factory, renewedAccount.AccountId, MlItem.NewIds(1), cancellationToken);

        var rejectedAccount = await Seed(expiring);
        fake.Reset();
        fake.TokenFails(400, "invalid_grant");
        exceptions.Add(await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, rejectedAccount.AccountId, MlItem.NewIds(1), cancellationToken)));

        var failedAccount = await Seed(expiring);
        fake.Reset();
        fake.TokenFails(500, "internal_error");
        exceptions.Add(await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, failedAccount.AccountId, MlItem.NewIds(1), cancellationToken)));

        // E os de IT-05: 401 com renovação que resolve e 401 que persiste
        var rejectedOnceAccount = await Seed(DateTimeOffset.UtcNow.AddHours(6));
        var renewedAfter401 = Remember(MlTokens.New());
        fake.Reset();
        fake.TokenResponds(renewedAfter401, rejectedOnceAccount.Seller.Id);
        fake.BulkResponds(requiredToken: renewedAfter401.AccessToken);
        await MercadoLivreGateway.GetItemsAsync(factory, rejectedOnceAccount.AccountId, MlItem.NewIds(1), cancellationToken);

        var alwaysRejectedAccount = await Seed(DateTimeOffset.UtcNow.AddHours(6));
        var renewedThenRejected = Remember(MlTokens.New());
        fake.Reset();
        fake.TokenResponds(renewedThenRejected, alwaysRejectedAccount.Seller.Id);
        fake.BulkAlwaysFails(401);
        exceptions.Add(await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, alwaysRejectedAccount.AccountId, MlItem.NewIds(1), cancellationToken)));

        // Quando os logs capturados (antes de qualquer máscara) e as exceções são inspecionados
        var records = logs.Records;
        Assert.NotEmpty(records);

        // Então nenhum contém access token, refresh token (antigos ou novos) nem o client secret
        foreach (var record in records)
        {
            var text = record.ToString();
            foreach (var (name, value) in secrets)
            {
                Assert.False(text.Contains(value, StringComparison.Ordinal), $"{name} apareceu em log:\n{text}");
            }
        }

        foreach (var exception in exceptions)
        {
            var text = exception.ToString();
            foreach (var (name, value) in secrets)
            {
                Assert.False(text.Contains(value, StringComparison.Ordinal), $"{name} apareceu na exceção:\n{text}");
            }
        }

        Task<SeededAccount> Seed(DateTimeOffset expiresAt) =>
            MercadoLivreGateway.SeedAccountAsync(factory, MlSeller.New(), Remember(MlTokens.New()), expiresAt, cancellationToken);

        MlTokens Remember(MlTokens tokens)
        {
            secrets.Add(("access token", tokens.AccessToken));
            secrets.Add(("refresh token", tokens.RefreshToken));
            return tokens;
        }
    }
}
