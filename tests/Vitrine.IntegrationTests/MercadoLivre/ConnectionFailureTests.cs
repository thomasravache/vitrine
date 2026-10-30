using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>Falhas do ML na conclusão da conexão (contrato da SPEC-0020): nada é gravado na tabela de contas.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConnectionFailureTests(PostgresFixture postgres)
{
    /// <summary>Resposta do ML simulado na conclusão.</summary>
    public enum MlFailure
    {
        /// <summary><c>POST /oauth/token</c> → 400 <c>invalid_grant</c> (code recusado).</summary>
        InvalidGrant,

        /// <summary><c>POST /oauth/token</c> → 500.</summary>
        ServerError,

        /// <summary><c>POST /oauth/token</c> não responde a tempo (timeout total de 10 s).</summary>
        Timeout,

        /// <summary><c>POST /oauth/token</c> → 429.</summary>
        TooManyRequests,

        /// <summary><c>POST /oauth/token</c> → 400 <c>invalid_client</c> (erro de cliente que não é do code).</summary>
        InvalidClient,

        /// <summary>Resposta sem <c>refresh_token</c> (app sem <c>offline_access</c>).</summary>
        MissingRefreshToken,

        /// <summary><c>/users/me</c> com <c>site_id=MLA</c>.</summary>
        OtherSite,

        /// <summary><c>/users/me</c> com <c>id</c> diferente do <c>user_id</c> do token.</summary>
        UserIdMismatch,
    }

    public static TheoryData<MlFailure, HttpStatusCode, string> Failures() => new()
    {
        { MlFailure.InvalidGrant, HttpStatusCode.BadRequest, "OAUTH_CODE_INVALID" },
        { MlFailure.ServerError, HttpStatusCode.BadGateway, "ML_UNAVAILABLE" },
        { MlFailure.Timeout, HttpStatusCode.BadGateway, "ML_UNAVAILABLE" },
        { MlFailure.TooManyRequests, HttpStatusCode.BadGateway, "ML_UNAVAILABLE" },
        { MlFailure.InvalidClient, HttpStatusCode.BadGateway, "ML_UNAVAILABLE" },
        { MlFailure.MissingRefreshToken, HttpStatusCode.BadGateway, "ML_UNAVAILABLE" },
        { MlFailure.OtherSite, (HttpStatusCode)422, "ML_SITE_NOT_SUPPORTED" },
        { MlFailure.UserIdMismatch, HttpStatusCode.BadGateway, "ML_UNAVAILABLE" },
    };

    // Timeout total da conclusão: 10 s (Http.Resilience); margem para o host de teste.
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TimeoutMargin = TimeSpan.FromSeconds(5);

    [Theory]
    [Trait("spec", "SPEC-0020:IT-04")]
    [MemberData(nameof(Failures))]
    public async Task Falha_do_ML_na_conclusao_responde_o_erro_do_contrato_e_nada_e_gravado(MlFailure failure, HttpStatusCode status, string code)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var logs = new RecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(connectionString).WithLogs(logs);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);
        var seller = MlSeller.New(failure == MlFailure.OtherSite ? "MLA" : "MLB");
        var tokens = MlTokens.New();

        // Dado o ML simulado respondendo a falha
        Arrange(fake, failure, seller, tokens);
        var started = await api.StartSuccessfullyAsync(cancellationToken);

        // Quando o Owner conclui a conexão
        var elapsed = Stopwatch.StartNew();
        using var response = await api.CompleteAsync(MercadoLivreApi.NewCode(), started.State, cancellationToken);
        elapsed.Stop();

        // Então o erro do contrato, e nada gravado na tabela de contas
        await IdentityApi.AssertProblemAsync(status, code, response, cancellationToken);
        Assert.Equal(0, await MercadoLivreDatabase.CountAccountsAsync(connectionString, cancellationToken));
        Assert.Empty(await api.ListSuccessfullyAsync(cancellationToken));

        if (failure == MlFailure.Timeout)
        {
            // E a espera respeita o timeout total de 10 s
            Assert.True(elapsed.Elapsed < TotalTimeout + TimeoutMargin, $"A conclusão esperou {elapsed.Elapsed} pelo ML (limite: {TotalTimeout}).");
        }

        if (failure == MlFailure.InvalidClient)
        {
            // E um log Error com o "error" do ML (sem segredos — IT-07)
            Assert.Contains(logs.Records, log => log.Level == LogLevel.Error && log.Message.Contains("invalid_client", StringComparison.Ordinal));
        }

        if (failure == MlFailure.MissingRefreshToken)
        {
            // E um log Error apontando o escopo offline_access ausente
            Assert.Contains(logs.Records, log => log.Level == LogLevel.Error && log.Message.Contains("offline_access", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-04")]
    public async Task Conta_do_ML_ja_conectada_em_outro_workspace_responde_409_ML_ACCOUNT_IN_USE_e_nada_e_gravado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        var seller = MlSeller.New();

        // Dado a conta do ML já conectada no workspace de outro Owner
        using var first = new MercadoLivreApi(factory);
        var firstOwner = await first.SignUpAsync(cancellationToken);
        var existing = await first.ConnectAsync(fake, seller, MlTokens.New(), cancellationToken);

        // Quando outro Owner conclui a conexão da mesma conta do ML
        using var second = new MercadoLivreApi(factory);
        await second.SignUpAsync(cancellationToken);
        fake.Reset();
        fake.Accepts(seller, MlTokens.New());
        var started = await second.StartSuccessfullyAsync(cancellationToken);
        using var response = await second.CompleteAsync(MercadoLivreApi.NewCode(), started.State, cancellationToken);

        // Então 409 ML_ACCOUNT_IN_USE; a tabela de contas continua só com a conta do primeiro workspace
        await IdentityApi.AssertProblemAsync(HttpStatusCode.Conflict, "ML_ACCOUNT_IN_USE", response, cancellationToken);
        var row = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
        Assert.Equal(existing.AccountId, row.Id);
        Assert.Equal(firstOwner.WorkspaceId, row.WorkspaceId);
        Assert.Empty(await second.ListSuccessfullyAsync(cancellationToken));
    }

    private static void Arrange(MercadoLivreFake fake, MlFailure failure, MlSeller seller, MlTokens tokens)
    {
        switch (failure)
        {
            case MlFailure.InvalidGrant:
                fake.TokenFails(400, "invalid_grant");
                break;
            case MlFailure.ServerError:
                fake.TokenFails(500, "internal_error");
                break;
            case MlFailure.Timeout:
                fake.TokenDelays(TotalTimeout + TimeSpan.FromSeconds(20), tokens, seller.Id);
                fake.UsersMeResponds(tokens.AccessToken, seller);
                break;
            case MlFailure.TooManyRequests:
                fake.TokenFails(429, "local_rate_limited");
                break;
            case MlFailure.InvalidClient:
                fake.TokenFails(400, "invalid_client");
                break;
            case MlFailure.MissingRefreshToken:
                fake.TokenResponds(tokens, seller.Id, withRefreshToken: false);
                fake.UsersMeResponds(tokens.AccessToken, seller);
                break;
            case MlFailure.OtherSite:
                fake.Accepts(seller, tokens);
                break;
            case MlFailure.UserIdMismatch:
                fake.TokenResponds(tokens, seller.Id + 1);
                fake.UsersMeResponds(tokens.AccessToken, seller);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, null);
        }
    }
}
