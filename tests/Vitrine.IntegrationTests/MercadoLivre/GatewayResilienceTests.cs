using System.Diagnostics;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Resiliência do cliente <c>mercadolivre-api</c> (SPEC-0021, ADR-0010): retry com <c>Retry-After</c>, até 3 tentativas e
/// circuit breaker. A espera entre tentativas fica curta e fixa nos testes (<see cref="MercadoLivreGateway.WithFastResilience"/>),
/// para que só o <c>Retry-After</c> do ML faça a chamada esperar.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GatewayResilienceTests(PostgresFixture postgres)
{
    /// <summary>Tentativas por chamada (seção 2 da SPEC-0021: "retry de até 3 tentativas"; IT-04: "depois de 3 tentativas").</summary>
    private const int Attempts = 3;

    [Fact]
    [Trait("spec", "SPEC-0021:IT-04")]
    public async Task Um_429_com_Retry_After_espera_e_conclui()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithFastResilience();
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado o ML respondendo 429 com Retry-After: 1 e depois 200
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.BulkFailsFirst(1, 429, retryAfter: "1");

        // Quando GetItemsAsync é chamado
        var elapsed = Stopwatch.StartNew();
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);
        elapsed.Stop();

        // Então a chamada conclui depois de ≥ 1 s, com 2 requisições ao ML
        Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        Assert.Equal(2, fake.BulkCalls.Count);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(990), $"A chamada não respeitou o Retry-After de 1 s ({elapsed.Elapsed}).");
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-04")]
    public async Task Um_503_persistente_lanca_Unavailable_depois_de_3_tentativas_sem_mudar_a_conta()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithFastResilience();
        var fake = new MercadoLivreFake(factory.ExternalServices);

        // Dado o ML respondendo 503 sempre
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.BulkAlwaysFails(503);

        // Quando GetItemsAsync é chamado; então lança Unavailable
        var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        Assert.Equal(account.AccountId, thrown.AccountId);

        // E houve exatamente 3 tentativas, sem renovação nem mudança da conta
        Assert.Equal(Attempts, fake.BulkCalls.Count);
        Assert.Empty(fake.TokenCalls);
        var stored = await MercadoLivreGateway.ReadAccountAsync(factory, account.AccountId, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(MercadoLivreAccountStatus.Connected, stored.Status);
        Assert.Equal(account.Tokens.AccessToken, stored.AccessToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-04")]
    public async Task Com_o_circuito_aberto_as_chamadas_seguintes_falham_sem_chegar_ao_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o circuito do mercadolivre-api abrindo com poucas chamadas (mínimo de 3 no teste) e o ML respondendo 503
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithFastResilience(circuitMinimumThroughput: Attempts);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        fake.BulkAlwaysFails(503);
        await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
            () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
        Assert.NotEmpty(fake.BulkCalls);
        fake.ForgetCalls();

        // Quando há novas chamadas enquanto o circuito está aberto
        var elapsed = Stopwatch.StartNew();
        for (var call = 0; call < 2; call++)
        {
            var thrown = await Assert.ThrowsAsync<MercadoLivreUnavailableException>(
                () => MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken));
            Assert.Equal(account.AccountId, thrown.AccountId);
        }

        elapsed.Stop();

        // Então falham rápido, sem nenhuma requisição chegar ao ML
        Assert.Empty(fake.BulkCalls);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Com o circuito aberto as chamadas deveriam falhar rápido ({elapsed.Elapsed}).");
    }
}
