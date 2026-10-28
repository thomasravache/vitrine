using System.Net;
using Microsoft.AspNetCore.Http;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Observability;

/// <summary>IT-05 (SPEC-0007): métricas da jornada de acesso no meter <c>Vitrine.Identity</c>.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthMetricsTests(PostgresFixture postgres)
{
    private const int MaxFailedAttempts = 5;
    private static readonly string[] ContractResults = ["success", "failure", "locked"];

    [Fact]
    [Trait("spec", "SPEC-0007:IT-05")]
    public async Task Login_ok_falho_e_bloqueio_medem_logins_por_result_e_lockouts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um MeterListener de teste no meter Vitrine.Identity e a Api com dois usuários existentes
        using var metrics = new AuthMetricsListener();
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var user = TestUser.New("Marina Souza");
        var lockedUser = TestUser.New("Paulo Lima");
        using (var registering = new IdentityApi(factory))
        {
            await registering.RegisterSuccessfullyAsync(user, cancellationToken);
        }

        using (var registering = new IdentityApi(factory))
        {
            await registering.RegisterSuccessfullyAsync(lockedUser, cancellationToken);
        }

        // Quando ocorre um login bem-sucedido
        using (var api = new IdentityApi(factory))
        using (var ok = await api.LoginAsync(user.Email, user.Password, cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, ok, cancellationToken);
        }

        // Então vitrine.auth.logins{result=success} conta exatamente esse login
        Assert.True(metrics.Total(AuthMetricsListener.Logins, "success") == 1, $"Esperado logins{{result=success}} = 1. {metrics.Describe()}");

        // Quando ocorre um login falho (senha errada)
        using (var api = new IdentityApi(factory))
        using (var failed = await api.LoginAsync(user.Email, "senha-errada-IT05", cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS", failed, cancellationToken);
        }

        // Então vitrine.auth.logins{result=failure} conta exatamente essa falha
        Assert.True(metrics.Total(AuthMetricsListener.Logins, "failure") == 1, $"Esperado logins{{result=failure}} = 1. {metrics.Describe()}");
        Assert.True(metrics.Total(AuthMetricsListener.Lockouts) == 0, $"Uma falha isolada não bloqueia a conta. {metrics.Describe()}");

        // Quando outra conta é bloqueada (5 falhas seguidas) e tenta de novo
        using (var api = new IdentityApi(factory))
        {
            for (var attempt = 1; attempt <= MaxFailedAttempts; attempt++)
            {
                using var wrong = await api.LoginAsync(lockedUser.Email, $"senha-errada-{attempt:00}", cancellationToken);
                Assert.True(
                    wrong.StatusCode is HttpStatusCode.Unauthorized or (HttpStatusCode)StatusCodes.Status423Locked,
                    $"Tentativa errada {attempt} respondeu {(int)wrong.StatusCode}.");
            }

            using var locked = await api.LoginAsync(lockedUser.Email, lockedUser.Password, cancellationToken);
            await IdentityApi.AssertProblemAsync((HttpStatusCode)StatusCodes.Status423Locked, "LOCKED_OUT", locked, cancellationToken);
        }

        // Então logins registra result=locked, vitrine.auth.lockouts incrementa, e result só assume os valores do contrato
        Assert.True(metrics.Total(AuthMetricsListener.Logins, "locked") >= 1, $"Esperado logins{{result=locked}} >= 1. {metrics.Describe()}");
        Assert.True(metrics.Total(AuthMetricsListener.Lockouts) >= 1, $"Esperado vitrine.auth.lockouts >= 1. {metrics.Describe()}");
        Assert.True(metrics.Total(AuthMetricsListener.Logins, "success") == 1, $"Bloqueio não pode contar como sucesso. {metrics.Describe()}");
        Assert.All(metrics.Results(AuthMetricsListener.Logins), result => Assert.Contains(result, ContractResults));

        // E toda medição de logins é das 6 tentativas da conta bloqueada + 1 sucesso + 1 falha
        Assert.True(
            metrics.Total(AuthMetricsListener.Logins) == MaxFailedAttempts + 3,
            $"Esperado {MaxFailedAttempts + 3} medições de logins (uma por tentativa). {metrics.Describe()}");
    }
}
