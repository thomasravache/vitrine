using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Infrastructure;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Métrica <c>vitrine.mercadolivre.token_refreshes{result}</c> do gateway (SPEC-0021, emenda v1.1): uma medição por
/// renovação, com <c>renewed</c>, <c>reauthorization_required</c> ou <c>unavailable</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewayMetricsTests(PostgresFixture postgres)
{
    private const string TokenRefreshes = "vitrine.mercadolivre.token_refreshes";

    [Fact]
    [Trait("spec", "SPEC-0021:IT-03")]
    public async Task Cada_renovacao_incrementa_token_refreshes_com_o_resultado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado um MeterListener no meter Vitrine.MercadoLivre desta Api (escopo do IMeterFactory do host)
        using var metrics = new AuthMetricsListener(factory.Services.GetRequiredService<IMeterFactory>(), MercadoLivreModule.MeterName);
        var expiring = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Quando 5 chamadas paralelas de uma conta com token expirado renovam (uma única vez)
        var renewedAccount = await MercadoLivreGateway.SeedAccountAsync(factory, MlSeller.New(), MlTokens.New(), expiring, cancellationToken);
        var renewed = MlTokens.New();
        fake.TokenResponds(renewed, renewedAccount.Seller.Id, delay: TimeSpan.FromMilliseconds(300));
        fake.BulkResponds(requiredToken: renewed.AccessToken);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(
            () => MercadoLivreGateway.GetItemsAsync(factory, renewedAccount.AccountId, MlItem.NewIds(1), cancellationToken),
            cancellationToken)));

        // Então token_refreshes{result=renewed} = 1
        Assert.True(metrics.Total(TokenRefreshes, "renewed") == 1, $"Esperado {TokenRefreshes}{{renewed}} = 1. {metrics.Describe()}");

        // Quando o ML recusa o refresh de outra conta (invalid_grant)
        var rejectedAccount = await MercadoLivreGateway.SeedAccountAsync(factory, MlSeller.New(), MlTokens.New(), expiring, cancellationToken);
        fake.Reset();
        fake.TokenFails(400, "invalid_grant");
        await Assert.ThrowsAsync<MercadoLivreReauthorizationRequiredException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, rejectedAccount.AccountId, MlItem.NewIds(1), cancellationToken));

        // Então token_refreshes{result=reauthorization_required} = 1
        Assert.True(
            metrics.Total(TokenRefreshes, "reauthorization_required") == 1,
            $"Esperado {TokenRefreshes}{{reauthorization_required}} = 1. {metrics.Describe()}");

        // Quando o /oauth/token de uma terceira conta responde 500
        var failedAccount = await MercadoLivreGateway.SeedAccountAsync(factory, MlSeller.New(), MlTokens.New(), expiring, cancellationToken);
        fake.Reset();
        fake.TokenFails(500, "internal_error");
        await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, failedAccount.AccountId, MlItem.NewIds(1), cancellationToken));

        // Então token_refreshes{result=unavailable} = 1, e só os três resultados do contrato foram medidos
        Assert.True(metrics.Total(TokenRefreshes, "unavailable") == 1, $"Esperado {TokenRefreshes}{{unavailable}} = 1. {metrics.Describe()}");
        Assert.Equal(3, metrics.Total(TokenRefreshes));
        Assert.Equal(
            ["reauthorization_required", "renewed", "unavailable"],
            metrics.Results(TokenRefreshes).Order(StringComparer.Ordinal));
    }
}
