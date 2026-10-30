using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.Common.Security;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>Iniciar e concluir a conexão OAuth com PKCE (contrato da SPEC-0020), com o ML simulado pelo WireMock.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConnectionTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0020:IT-01")]
    public async Task Owner_inicia_e_conclui_a_conexao_e_a_conta_e_gravada_com_tokens_cifrados_e_evento_no_outbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var seller = MlSeller.New();
        var tokens = MlTokens.New();
        Assert.True(seller.Id > int.MaxValue, "O vendedor de teste precisa de id acima de Int32.");

        // O assinante de teste fica segurado: o evento permanece no armazenamento durável enquanto é inspecionado.
        ConnectedAccount account;
        using (MercadoLivreEventsTestHandler.Hold())
        {
            // Dado Postgres real e o ML simulado com /oauth/token e /users/me válidos (MLB)
            fake.Accepts(seller, tokens);

            // Quando o Owner inicia e conclui a conexão com o code devolvido pelo ML e o state recebido
            var started = await api.StartSuccessfullyAsync(cancellationToken);
            var code = MercadoLivreApi.NewCode();
            using var response = await api.CompleteAsync(code, started.State, cancellationToken);

            // Então 201 com accountId, mlUserId (int64 acima de Int32), nickname e status connected
            await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, response, cancellationToken);
            account = await ConnectedAccount.ReadAsync(response, cancellationToken);
            Assert.NotEqual(Guid.Empty, account.AccountId);
            Assert.Equal(seller.Id, account.MlUserId);
            Assert.Equal(seller.Nickname, account.Nickname);
            Assert.Equal("connected", account.Status);

            // E o ML recebeu a troca do code com grant_type, client_id, client_secret, code, redirect_uri e o
            // code_verifier cujo SHA-256 é o code_challenge enviado na URL de autorização
            var tokenCall = Assert.Single(fake.TokenCalls);
            Assert.Equal("application/x-www-form-urlencoded", MediaType(tokenCall));
            var form = fake.LastTokenForm();
            Assert.Equal("authorization_code", form.GetValueOrDefault("grant_type"));
            Assert.Equal(MercadoLivreHost.ClientId, form.GetValueOrDefault("client_id"));
            Assert.Equal(MercadoLivreHost.ClientSecret, form.GetValueOrDefault("client_secret"));
            Assert.Equal(code, form.GetValueOrDefault("code"));
            Assert.Equal(MercadoLivreHost.RedirectUri, form.GetValueOrDefault("redirect_uri"));
            var verifier = form.GetValueOrDefault("code_verifier") ?? string.Empty;
            Assert.Equal(64, verifier.Length);
            Assert.Equal(started.CodeChallenge, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));

            // E a conta foi gravada no workspace do Owner com os tokens cifrados (propósito mercadolivre.tokens)
            var row = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
            Assert.Equal(account.AccountId, row.Id);
            Assert.Equal(owner.WorkspaceId, row.WorkspaceId);
            Assert.Equal(seller.Id, row.MlUserId);
            Assert.Equal("MLB", row.SiteId);
            AssertEncrypted(factory, tokens.AccessToken, row.AccessToken);
            AssertEncrypted(factory, tokens.RefreshToken, row.RefreshToken);

            // E nenhum dado pessoal do vendedor devolvido por /users/me foi guardado (só id, nickname e site)
            var stored = await MercadoLivreDatabase.DumpAsync(connectionString, "accounts", cancellationToken);
            Assert.DoesNotContain(MercadoLivreFake.PersonalEmail(seller), stored, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ficticia", stored, StringComparison.Ordinal);

            // E MercadoLivreAccountConnected está no outbox (armazenamento durável do Wolverine) junto com a conta
            Assert.True(
                await MercadoLivreDatabase.CountEnvelopesAsync(connectionString, nameof(MercadoLivreAccountConnected), account.AccountId, cancellationToken) >= 1,
                "MercadoLivreAccountConnected da conta conectada não está no armazenamento durável do Wolverine (outbox).");
        }

        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountConnected>(account.AccountId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreAccountConnected da conta conectada",
            cancellationToken);
        Assert.False(Assert.Single(MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountConnected>(account.AccountId)).Reconnected);

        // Quando a mesma conta do ML é conectada de novo no mesmo workspace, com tokens novos
        var newTokens = MlTokens.New();
        var reconnected = await api.ConnectAsync(fake, seller, newTokens, cancellationToken, HttpStatusCode.OK);

        // Então 200 com a mesma conta, connected, e os tokens trocados (continua uma linha só)
        Assert.Equal(account.AccountId, reconnected.AccountId);
        Assert.Equal(seller.Id, reconnected.MlUserId);
        Assert.Equal("connected", reconnected.Status);
        var updated = Assert.Single(await MercadoLivreDatabase.ReadAccountsAsync(connectionString, cancellationToken));
        AssertEncrypted(factory, newTokens.AccessToken, updated.AccessToken);
        AssertEncrypted(factory, newTokens.RefreshToken, updated.RefreshToken);

        // E o evento da reconexão sai com reconnected = true
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ReceivedFor<MercadoLivreAccountConnected>(account.AccountId).Any(message => message.Reconnected),
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreAccountConnected com reconnected = true",
            cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-02")]
    public async Task Owner_inicia_a_conexao_e_recebe_a_URL_de_autorizacao_com_PKCE_S256_e_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        using var api = new MercadoLivreApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);

        // Dado a configuração completa; quando o Owner chama POST /connections
        var started = await api.StartSuccessfullyAsync(cancellationToken);

        // Então a URL aponta para a autorização oficial do ML com response_type, client_id, redirect_uri, state,
        // code_challenge (base64url de SHA-256: 43 caracteres) e code_challenge_method=S256
        Assert.Equal("https://auth.mercadolivre.com.br/authorization", started.AuthorizationUrl.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", started.Query.GetValueOrDefault("response_type"));
        Assert.Equal(MercadoLivreHost.ClientId, started.Query.GetValueOrDefault("client_id"));
        Assert.Equal(MercadoLivreHost.RedirectUri, started.Query.GetValueOrDefault("redirect_uri"));
        Assert.False(string.IsNullOrEmpty(started.State), "URL de autorização sem state.");
        Assert.Matches("^[A-Za-z0-9_-]{43}$", started.CodeChallenge);
        Assert.Equal("S256", started.Query.GetValueOrDefault("code_challenge_method"));

        // E a autorização pendente foi gravada para o usuário e o workspace, com o state só como hash e validade de 10 min
        var pending = Assert.Single(await MercadoLivreDatabase.ReadPendingAuthorizationsAsync(connectionString, cancellationToken));
        Assert.Equal(owner.UserId, pending.UserId);
        Assert.Equal(owner.WorkspaceId, pending.WorkspaceId);
        Assert.DoesNotContain(started.State, pending.StateHash, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(pending.CodeVerifier), "Autorização pendente sem code_verifier.");
        Assert.Null(pending.UsedAt);
        Assert.InRange(pending.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(5));

        // E duas iniciações seguidas geram states distintos
        var again = await api.StartSuccessfullyAsync(cancellationToken);
        Assert.NotEqual(started.State, again.State);
        Assert.NotEqual(started.CodeChallenge, again.CodeChallenge);
    }

    [Theory]
    [Trait("spec", "SPEC-0020:IT-02")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("RedirectUri")]
    public async Task Sem_configuracao_obrigatoria_do_ML_iniciar_e_concluir_respondem_503_ML_NOT_CONFIGURED(string missing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado a Api sem uma das configurações obrigatórias do ML
        await using var factory = MercadoLivreHost.Create(connectionString, omit: missing);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);

        // Quando o Owner inicia a conexão; então 503 ML_NOT_CONFIGURED e nada gravado
        using (var start = await api.StartAsync(cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "ML_NOT_CONFIGURED", start, cancellationToken);
        }

        Assert.Empty(await MercadoLivreDatabase.ReadPendingAuthorizationsAsync(connectionString, cancellationToken));

        // E concluir também responde 503 ML_NOT_CONFIGURED, sem chamar o ML
        using (var complete = await api.CompleteAsync(MercadoLivreApi.NewCode(), "state-qualquer", cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "ML_NOT_CONFIGURED", complete, cancellationToken);
        }

        Assert.Empty(fake.Calls);
    }

    public static TheoryData<string, string[]> InvalidCompleteBodies() => new()
    {
        { "", ["code", "state"] },
        { "{}", ["code", "state"] },
        { "{\"code\":\"\",\"state\":\"\"}", ["code", "state"] },
        { "{\"code\":\"" + new string('c', 513) + "\",\"state\":\"state-valido\"}", ["code"] },
        { "{\"code\":\"TG-code\",\"state\":\"" + new string('s', 129) + "\"}", ["state"] },
    };

    [Theory]
    [Trait("spec", "SPEC-0020:IT-03")]
    [MemberData(nameof(InvalidCompleteBodies))]
    public async Task Complete_com_corpo_ausente_ou_campos_fora_do_tamanho_responde_400_VALIDATION_FAILED_com_errors(string rawBody, string[] invalidFields)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);

        // Dado o Owner com uma autorização pendente; quando POST /complete chega com corpo ausente ou inválido
        _ = await api.StartSuccessfullyAsync(cancellationToken);
        using var response = await api.Http.PostRawJsonAsync(MercadoLivreApi.CompletePath, rawBody, cancellationToken);

        // Então 400 VALIDATION_FAILED com errors só nos campos inválidos (code 1..512, state 1..128), sem chamar o ML
        var problem = await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED", response, cancellationToken);
        Assert.True(problem.TryGetProperty("errors", out var errors), $"Problem sem errors: {problem}");
        Assert.Equal(invalidFields.Order(StringComparer.Ordinal), errors.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        Assert.Empty(fake.Calls);
    }

    /// <summary>Como o state chega inválido em <c>POST /complete</c>.</summary>
    public enum InvalidState
    {
        /// <summary>State que nunca foi emitido.</summary>
        Unknown,

        /// <summary>State emitido há mais de 10 minutos (relógio do host avançado).</summary>
        Expired,

        /// <summary>State já usado numa conclusão anterior.</summary>
        AlreadyUsed,

        /// <summary>State emitido para outro usuário.</summary>
        OtherUser,
    }

    [Theory]
    [Trait("spec", "SPEC-0020:IT-03")]
    [InlineData(InvalidState.Unknown)]
    [InlineData(InvalidState.Expired)]
    [InlineData(InvalidState.AlreadyUsed)]
    [InlineData(InvalidState.OtherUser)]
    public async Task State_invalido_responde_400_OAUTH_STATE_INVALID_sem_chamar_o_ML(InvalidState variant)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var clock = new AdjustableClock();
        await using var factory = MercadoLivreHost.Create(connectionString).WithClock(clock);
        var fake = new MercadoLivreFake(factory.ExternalServices);
        using var api = new MercadoLivreApi(factory);
        await api.SignUpAsync(cancellationToken);
        fake.Accepts(MlSeller.New(), MlTokens.New());

        // Dado um state inexistente, expirado, já usado ou criado por outro usuário
        var state = variant switch
        {
            InvalidState.Unknown => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            InvalidState.Expired => await ExpiredStateAsync(api, clock, cancellationToken),
            InvalidState.AlreadyUsed => await UsedStateAsync(api, cancellationToken),
            InvalidState.OtherUser => await OtherUsersStateAsync(factory, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
        fake.ForgetCalls();

        // Quando o Owner conclui com esse state
        using var response = await api.CompleteAsync(MercadoLivreApi.NewCode(), state, cancellationToken);

        // Então 400 OAUTH_STATE_INVALID, o ML não recebeu nenhuma chamada e nenhuma conta nova foi gravada
        await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "OAUTH_STATE_INVALID", response, cancellationToken);
        Assert.Empty(fake.Calls);
        var expectedAccounts = variant == InvalidState.AlreadyUsed ? 1 : 0;
        Assert.Equal(expectedAccounts, await MercadoLivreDatabase.CountAccountsAsync(connectionString, cancellationToken));
    }

    private static async Task<string> ExpiredStateAsync(MercadoLivreApi api, AdjustableClock clock, CancellationToken cancellationToken)
    {
        var started = await api.StartSuccessfullyAsync(cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        return started.State;
    }

    private static async Task<string> UsedStateAsync(MercadoLivreApi api, CancellationToken cancellationToken)
    {
        var started = await api.StartSuccessfullyAsync(cancellationToken);
        using var first = await api.CompleteAsync(MercadoLivreApi.NewCode(), started.State, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, first, cancellationToken);
        return started.State;
    }

    private static async Task<string> OtherUsersStateAsync(ApiFactory factory, CancellationToken cancellationToken)
    {
        using var other = new MercadoLivreApi(factory);
        await other.SignUpAsync(cancellationToken);
        return (await other.StartSuccessfullyAsync(cancellationToken)).State;
    }

    private static void AssertEncrypted(ApiFactory factory, string plaintext, string stored)
    {
        Assert.False(string.IsNullOrEmpty(stored), "Coluna de token vazia.");
        Assert.DoesNotContain(plaintext, stored, StringComparison.Ordinal);
        var protector = factory.Services.GetRequiredService<IFieldProtector>();
        Assert.Equal(plaintext, protector.Unprotect(stored, MercadoLivreHost.TokensPurpose));
    }

    private static string? MediaType(WireMock.Logging.ILogEntry entry) =>
        entry.RequestMessage?.Headers?
            .Where(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .Select(value => value.Split(';')[0].Trim())
            .FirstOrDefault();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
