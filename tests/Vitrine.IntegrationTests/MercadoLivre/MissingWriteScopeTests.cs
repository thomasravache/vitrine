using System.Net;
using Microsoft.Extensions.Logging;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Aviso de escopo <c>write</c> ausente na troca do code (SPEC-0033): o ML não recebe escopo na URL de autorização, então
/// o Vitrine só avisa no log quando o token chega sem <c>write</c>; a conexão segue como na SPEC-0020.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MissingWriteScopeTests(PostgresFixture postgres)
{
    private const string MissingWriteScope = "MissingWriteScope";

    [Fact]
    [Trait("spec", "SPEC-0033:IT-08")]
    public async Task Troca_do_code_sem_write_no_escopo_avisa_MissingWriteScope_e_a_conexao_segue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new EventRecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithLogs(logs);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);

        // Dado a troca do code devolvendo scope "offline_access read" (sem write)
        var seller = MlSeller.New();
        var tokens = MlTokens.New();
        fake.TokenResponds(tokens, seller.Id, scope: "offline_access read");
        fake.UsersMeResponds(tokens.AccessToken, seller);

        // Quando a conexão é concluída
        var started = await api.StartSuccessfullyAsync(cancellationToken);
        using var response = await api.CompleteAsync(MercadoLivreApi.NewCode(), started.State, cancellationToken);

        // Então a conexão segue como na SPEC-0020 (201, connected)
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, response, cancellationToken);
        var account = await ConnectedAccount.ReadAsync(response, cancellationToken);
        Assert.Equal("connected", account.Status);

        // E sai um log Warning MissingWriteScope, sem dados do token
        var warning = Assert.Single(logs.Records, record => record.EventId.Name == MissingWriteScope);
        Assert.Equal(LogLevel.Warning, warning.Level);
        var text = warning.ToString();
        Assert.DoesNotContain(tokens.AccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(tokens.RefreshToken, text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:IT-08")]
    public async Task Troca_do_code_com_write_no_escopo_nao_avisa()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new EventRecordingLoggerProvider();
        await using var factory = MercadoLivreHost.Create(await postgres.CreateDatabaseAsync(cancellationToken)).WithLogs(logs);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);

        // Dado a troca do code devolvendo scope "offline_access read write"
        var seller = MlSeller.New();
        var tokens = MlTokens.New();
        fake.TokenResponds(tokens, seller.Id, scope: "offline_access read write");
        fake.UsersMeResponds(tokens.AccessToken, seller);

        // Quando a conexão é concluída
        var started = await api.StartSuccessfullyAsync(cancellationToken);
        using var response = await api.CompleteAsync(MercadoLivreApi.NewCode(), started.State, cancellationToken);

        // Então a conexão é criada e não há aviso MissingWriteScope
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, response, cancellationToken);
        Assert.DoesNotContain(logs.Records, record => record.EventId.Name == MissingWriteScope);
    }
}
