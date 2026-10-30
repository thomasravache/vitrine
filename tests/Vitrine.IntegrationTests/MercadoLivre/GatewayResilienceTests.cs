using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
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
    [Fact]
    [Trait("spec", "SPEC-0021:IT-04")]
    public async Task Pipeline_do_mercadolivre_api_tem_3_tentativas_timeout_de_10_s_por_tentativa_e_60_s_no_total()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken));

        // Quando as opções do pipeline padrão do cliente mercadolivre-api são lidas no host
        var options = factory.Services.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(MercadoLivreGateway.ApiResilienceOptionsName);

        // Então são as da seção 2 da SPEC-0021 (emenda v1.1)
        Assert.Equal(Attempts - 1, options.Retry.MaxRetryAttempts);
        Assert.True(options.Retry.ShouldRetryAfterHeader, "O retry precisa respeitar o Retry-After.");
        Assert.Equal(TimeSpan.FromSeconds(10), options.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.TotalRequestTimeout.Timeout);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-04")]
    public async Task Retry_After_acima_de_30_s_e_limitado_a_30_s()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um relógio que acelera as esperas longas (30 s viram 1,5 s) e registra cada prazo pedido
        var clock = new AcceleratedTimeProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithClock(clock);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // E o ML respondendo 429 com Retry-After: 120 e depois 200
        fake.BulkFailsFirst(1, 429, retryAfter: "120");
        clock.ForgetRequested();

        // Quando GetItemsAsync é chamado
        var elapsed = Stopwatch.StartNew();
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);
        elapsed.Stop();

        // Então a espera pedida foi de 30 s (o teto), nunca de 120 s, e a chamada concluiu na 2ª tentativa
        // (sem o teto, a espera de 120 s passaria do timeout total de 60 s e a chamada falharia)
        Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        Assert.Equal(2, fake.BulkCalls.Count);
        Assert.Contains(TimeSpan.FromSeconds(30), clock.Requested);
        Assert.DoesNotContain(TimeSpan.FromSeconds(120), clock.Requested);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"A espera não foi limitada a 30 s ({elapsed.Elapsed} com o relógio acelerado).");
    }

    [Fact]
    [Trait("spec", "SPEC-0021:IT-04")]
    public async Task Tentativa_que_passa_de_10_s_e_cancelada_e_retentada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um relógio que acelera os prazos longos (o timeout de 10 s vira 0,5 s) e registra cada prazo pedido
        var clock = new AcceleratedTimeProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithClock(clock)
            .WithFastResilience();
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);

        // E o ML demorando na 1ª chamada (8 s reais, 160 s no relógio acelerado) e respondendo na hora na 2ª
        fake.BulkDelaysFirst(1, TimeSpan.FromSeconds(8));
        clock.ForgetRequested();

        // Quando GetItemsAsync é chamado
        var elapsed = Stopwatch.StartNew();
        var results = await MercadoLivreGateway.GetItemsAsync(factory, account.AccountId, MlItem.NewIds(1), cancellationToken);
        elapsed.Stop();

        // Então a 1ª tentativa foi cancelada pelo timeout de 10 s e a 2ª concluiu, sem esperar a resposta lenta
        // (contadas na chegada: a requisição lenta só entra no log do WireMock quando a resposta dela fica pronta)
        Assert.IsType<MercadoLivreItemResult.Found>(Assert.Single(results));
        Assert.Equal(2, fake.BulkArrivals);
        Assert.Contains(TimeSpan.FromSeconds(10), clock.Requested);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(6), $"A tentativa lenta não foi cancelada pelo timeout ({elapsed.Elapsed}).");
    }
}

