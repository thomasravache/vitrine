using System.Diagnostics;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Caracterização das leituras do cliente <c>mercadolivre-api</c> (SPEC-0033:CH-01): a busca de IDs e o
/// <c>/items/bulk</c> da SPEC-0021 continuam com o retry de GET (429 com <c>Retry-After</c>, 3 tentativas no 503) e o
/// circuit breaker quando o pipeline passa a ignorar métodos não seguros (<c>DisableForUnsafeHttpMethods</c>). Passa
/// antes da mudança e precisa continuar passando depois dela.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewayReadResilienceCharacterizationTests(PostgresFixture postgres)
{
    /// <summary>Tentativas por leitura (SPEC-0021, seção 2, emenda v1.1).</summary>
    private const int Attempts = 3;

    public enum ReadEndpoint
    {
        Search,
        Bulk,
    }

    [Theory]
    [Trait("spec", "SPEC-0033:CH-01")]
    [InlineData(ReadEndpoint.Search)]
    [InlineData(ReadEndpoint.Bulk)]
    public async Task Leitura_com_429_e_Retry_After_espera_e_conclui_na_segunda_tentativa(ReadEndpoint endpoint)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithFastResilience();
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var ids = MlItem.NewIds(1);

        // Dado o ML respondendo à leitura 429 com Retry-After: 1 e depois 200
        if (endpoint == ReadEndpoint.Search)
        {
            fake.SearchFailsFirst(account.Seller.Id, 1, 429, ids, retryAfter: "1");
        }
        else
        {
            fake.BulkFailsFirst(1, 429, retryAfter: "1");
        }

        // Quando a leitura é feita pelo gateway
        var elapsed = Stopwatch.StartNew();
        if (endpoint == ReadEndpoint.Search)
        {
            var pages = await MercadoLivreGateway.SearchItemIdsAsync(factory, account.AccountId, "active", cancellationToken);
            Assert.Equal(ids, pages.SelectMany(page => page));
        }
        else
        {
            var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, ids, cancellationToken);
            Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        }

        elapsed.Stop();

        // Então o GET foi repetido uma vez, depois de respeitar o Retry-After de 1 s
        Assert.Equal(2, Calls(fake, endpoint, account));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(990), $"A leitura não respeitou o Retry-After de 1 s ({elapsed.Elapsed}).");
    }

    [Theory]
    [Trait("spec", "SPEC-0033:CH-01")]
    [InlineData(ReadEndpoint.Search)]
    [InlineData(ReadEndpoint.Bulk)]
    public async Task Leitura_com_503_persistente_lanca_Unavailable_depois_de_3_tentativas(ReadEndpoint endpoint)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithFastResilience();
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // Dado o ML respondendo 503 sempre à leitura
        if (endpoint == ReadEndpoint.Search)
        {
            fake.SearchAlwaysFails(account.Seller.Id, 503);
        }
        else
        {
            fake.BulkAlwaysFails(503);
        }

        // Quando a leitura é feita; então lança Unavailable com a conta
        var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(() => ReadAsync(factory, endpoint, account, cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);

        // E o GET foi tentado exatamente 3 vezes, sem renovação de token
        Assert.Equal(Attempts, Calls(fake, endpoint, account));
        Assert.Empty(fake.TokenCalls);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:CH-01")]
    public async Task Circuito_aberto_por_leituras_falha_as_leituras_seguintes_sem_chegar_ao_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o circuito do mercadolivre-api abrindo com poucas chamadas e a busca respondendo 503
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithFastResilience(circuitMinimumThroughput: Attempts);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.SearchAlwaysFails(account.Seller.Id, 503);
        fake.BulkAlwaysFails(503);
        await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => ReadAsync(factory, ReadEndpoint.Search, account, cancellationToken));
        Assert.True(fake.SearchArrivals > 0, "A busca deveria ter chegado ao ML antes de o circuito abrir.");
        fake.ForgetCalls();

        // Quando há novas leituras (busca e bulk) com o circuito aberto
        var elapsed = Stopwatch.StartNew();
        foreach (var endpoint in new[] { ReadEndpoint.Search, ReadEndpoint.Bulk })
        {
            var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(() => ReadAsync(factory, endpoint, account, cancellationToken));
            Assert.Equal(account.AccountId, thrown.AccountId);
        }

        elapsed.Stop();

        // Então falham rápido, sem nenhuma requisição chegar ao ML
        Assert.Empty(fake.Calls);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Com o circuito aberto as leituras deveriam falhar rápido ({elapsed.Elapsed}).");
    }

    private static int Calls(MercadoLivreFake fake, ReadEndpoint endpoint, SeededAccount account) =>
        endpoint == ReadEndpoint.Search ? fake.SearchCalls(account.Seller.Id).Count : fake.BulkCalls.Count;

    private static async Task ReadAsync(ApiFactory factory, ReadEndpoint endpoint, SeededAccount account, CancellationToken cancellationToken)
    {
        if (endpoint == ReadEndpoint.Search)
        {
            await MercadoLivreGateway.SearchItemIdsAsync(factory, account.AccountId, "active", cancellationToken);
        }
        else
        {
            await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);
        }
    }
}
