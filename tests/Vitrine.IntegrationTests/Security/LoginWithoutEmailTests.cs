using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// IT-01 (SPEC-0014): login sem exatamente um <c>email</c> string não vazio não tem partição no limite por e-mail e,
/// por isso, é recusado antes do Identity com 400 <c>VALIDATION_FAILED</c> e erro no campo <c>email</c> (contrato
/// SPEC-0006@1). O login com e-mail válido continua como antes (401 com senha errada, 204 com a certa).
/// Os casos compartilham um único host e banco (<see cref="LoginHost"/>): cada requisição vem de um IP próprio e os
/// e-mails são gerados por execução, então não interferem entre si nem esgotam as conexões do PostgreSQL compartilhado.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LoginWithoutEmailTests(LoginWithoutEmailTests.LoginHost host) : IClassFixture<LoginWithoutEmailTests.LoginHost>
{
    private const string PasswordPlaceholder = "__PASSWORD__";

    [Theory]
    [Trait("spec", "SPEC-0014:IT-01")]
    [InlineData("""{"email":null,"password":"__PASSWORD__"}""")]
    [InlineData("""{"email":"","password":"__PASSWORD__"}""")]
    [InlineData("""{"email":"   ","password":"__PASSWORD__"}""")]
    [InlineData("""{"email":123,"password":"__PASSWORD__"}""")]
    [InlineData("""{"password":"__PASSWORD__"}""")]
    public async Task Login_sem_email_valido_responde_400_VALIDATION_FAILED_com_erro_em_email(string template)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = ProxiedClients.ViaTrustedProxy(host.Factory, host.NextClient());

        // Dado a Api; quando o login chega com e-mail nulo, vazio, só espaços, não-string ou ausente
        var json = template.Replace(PasswordPlaceholder, "senha-errada-" + Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        using var response = await client.PostContentAsync(
            IdentityApi.LoginPath, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

        // Então 400 VALIDATION_FAILED com erro no campo email
        var problem = await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED", response, cancellationToken);
        Assert.True(problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object, $"Problem sem \"errors\": {problem}");
        var email = errors.EnumerateObject().Where(field => string.Equals(field.Name, "email", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(
            email.Count == 1
            && email[0].Value.ValueKind == JsonValueKind.Array
            && email[0].Value.GetArrayLength() > 0
            && email[0].Value.EnumerateArray().All(message => !string.IsNullOrWhiteSpace(message.GetString())),
            $"Problem sem erro no campo \"email\": {problem}");
    }

    [Fact]
    [Trait("spec", "SPEC-0014:IT-01")]
    public async Task Login_com_email_valido_continua_401_com_senha_errada_e_204_com_a_certa()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = TestUser.New();
        using (var owner = ProxiedClients.ViaTrustedProxy(host.Factory, host.NextClient()))
        {
            using var register = await owner.RegisterAsync(user, cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, register, cancellationToken);
        }

        using var client = ProxiedClients.ViaTrustedProxy(host.Factory, host.NextClient());

        // Dado uma conta; quando o login chega com o e-mail dela e a senha errada, então 401
        using (var wrong = await client.LoginAsync(user.Email, "senha-errada-" + Guid.NewGuid().ToString("N"), cancellationToken))
        {
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, wrong, cancellationToken);
        }

        // E com a senha certa, 204
        using var right = await client.LoginAsync(user.Email, user.Password, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NoContent, right, cancellationToken);
    }

    /// <summary>Um host (e um banco) para todos os casos da classe, descartado ao fim com o pool de conexões dele.</summary>
    public sealed class LoginHost(PostgresFixture postgres) : IAsyncLifetime
    {
        private int _nextClient;

        public ApiFactory Factory { get; private set; } = null!;

        /// <summary>Um IP de cliente ainda não usado nesta classe (o limite por IP nunca pesa entre casos).</summary>
        public System.Net.IPAddress NextClient() => TestAddresses.Client(Interlocked.Increment(ref _nextClient));

        public async ValueTask InitializeAsync()
        {
            var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
            Factory = IdentityHost.Create(connectionString).TrustingTestProxy();
        }

        public async ValueTask DisposeAsync()
        {
            var connectionString = Factory.ConnectionString;
            await Factory.DisposeAsync();
            using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }
    }
}
