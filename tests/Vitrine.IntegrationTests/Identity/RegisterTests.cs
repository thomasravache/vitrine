using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

[Collection(PostgresTestGroup.Name)]
public sealed class RegisterTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0006:IT-01")]
    public async Task Cadastro_valido_cria_usuario_workspace_Owner_sessao_e_WorkspaceCreated_no_outbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);
        var user = TestUser.New();

        // O assinante de teste fica segurado: a mensagem permanece no armazenamento durável enquanto é inspecionada.
        using (WorkspaceCreatedTestHandler.Hold())
        {
            // Dado Postgres real; quando POST /register com dados válidos
            using var response = await api.RegisterAsync(user, cancellationToken);

            // Então 201 com { userId, workspaceId } e cookie de sessão emitido
            var account = await IdentityApi.ReadRegisteredAccountAsync(response, cancellationToken);
            Assert.NotEqual(Guid.Empty, account.UserId);
            Assert.NotEqual(Guid.Empty, account.WorkspaceId);
            var sessionCookies = SetCookies.Session(response);
            Assert.True(sessionCookies.Count > 0, "O cadastro não emitiu cookie de sessão.");
            Assert.All(sessionCookies, cookie => Assert.True(cookie.HttpOnly, $"Cookie de sessão {cookie.Name} sem HttpOnly."));

            // E workspace e membro persistidos
            Assert.Equal(1, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));
            Assert.Equal(1, await IdentityDatabase.CountWorkspaceMembersAsync(connectionString, cancellationToken));

            // E WorkspaceCreated registrado no outbox (armazenamento durável do Wolverine) junto com o cadastro
            Assert.True(
                await IdentityDatabase.CountWorkspaceCreatedEnvelopesAsync(connectionString, account.WorkspaceId, cancellationToken) >= 1,
                "WorkspaceCreated do workspace cadastrado não está no armazenamento durável do Wolverine (outbox).");

            // E a sessão iniciada dá acesso ao usuário e ao workspace com papel Owner
            using var me = await api.GetMeAsync(cancellationToken);
            await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);
            var body = await IdentityApi.ReadJsonAsync(me, cancellationToken);
            Assert.Equal(account.UserId, body.GetProperty("userId").GetGuid());
            Assert.Equal(user.Name, body.GetProperty("name").GetString());
            Assert.Equal(user.Email, body.GetProperty("email").GetString());
            var workspace = body.GetProperty("workspace");
            Assert.Equal(account.WorkspaceId, workspace.GetProperty("id").GetGuid());
            Assert.False(string.IsNullOrWhiteSpace(workspace.GetProperty("name").GetString()), "Workspace sem nome.");
            Assert.Equal("Owner", workspace.GetProperty("role").GetString());
        }
    }

    public static TheoryData<string, string, string, string[]> InvalidRegistrations() => new()
    {
        { "", "vendedor.{0}@example.com", TestUser.DefaultPassword, ["name"] },
        { "Marina Souza", "nao-e-email-{0}", TestUser.DefaultPassword, ["email"] },
        { "Marina Souza", "vendedor.{0}@example.com", "123456789", ["password"] },
        { "", "nao-e-email-{0}", "123456789", ["email", "name", "password"] },
    };

    [Theory]
    [Trait("spec", "SPEC-0006:IT-02")]
    [MemberData(nameof(InvalidRegistrations))]
    public async Task Cadastro_invalido_responde_400_VALIDATION_FAILED_com_erros_por_campo_e_nada_e_persistido(
        string name, string emailPattern, string password, string[] invalidFields)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);
        var user = new TestUser(name, string.Format(System.Globalization.CultureInfo.InvariantCulture, emailPattern, Guid.NewGuid().ToString("N")), password);

        // Dado Postgres real; quando POST /register com dados inválidos
        using var response = await api.RegisterAsync(user, cancellationToken);

        // Então 400 VALIDATION_FAILED com um erro por campo inválido (e só neles)
        var problem = await IdentityApi.AssertProblemAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED", response, cancellationToken);
        Assert.True(problem.TryGetProperty("errors", out var errors), $"Problem sem \"errors\": {problem}");
        var fields = errors.EnumerateObject().ToDictionary(field => field.Name.ToLowerInvariant(), field => field.Value);
        Assert.Equal(invalidFields, fields.Keys.Order(StringComparer.Ordinal));
        Assert.All(fields.Values, messages => Assert.True(
            messages.GetArrayLength() > 0 && messages.EnumerateArray().All(m => !string.IsNullOrWhiteSpace(m.GetString())),
            $"Campo sem mensagem de erro: {messages}"));

        // E nada é persistido: nem workspace, nem membro, nem evento
        Assert.Equal(0, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));
        Assert.Equal(0, await IdentityDatabase.CountWorkspaceMembersAsync(connectionString, cancellationToken));
        Assert.Equal(0, await IdentityDatabase.CountWorkspaceCreatedEnvelopesAsync(connectionString, workspaceId: null, cancellationToken));

        // E, quando o e-mail era válido, ele continua livre (o usuário não foi criado)
        if (!invalidFields.Contains("email"))
        {
            using var retry = new IdentityApi(factory);
            await retry.RegisterSuccessfullyAsync(TestUser.New() with { Email = user.Email }, cancellationToken);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-02")]
    public async Task Cadastro_com_email_ja_usado_responde_409_EMAIL_TAKEN_e_nada_e_persistido()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var existing = TestUser.New("Marina Souza");
        using (var first = new IdentityApi(factory))
        {
            await first.RegisterSuccessfullyAsync(existing, cancellationToken);
        }

        // Dado um e-mail já cadastrado; quando outro visitante se cadastra com ele
        using var api = new IdentityApi(factory);
        var intruder = new TestUser("Carlos Lima", existing.Email, "outra-senha-forte");
        using var response = await api.RegisterAsync(intruder, cancellationToken);

        // Então 409 EMAIL_TAKEN, sem sessão
        await IdentityApi.AssertProblemAsync(HttpStatusCode.Conflict, "EMAIL_TAKEN", response, cancellationToken);
        Assert.Empty(SetCookies.Session(response));

        // E nada é persistido: continua um único workspace/membro, e a senha do usuário existente não mudou
        Assert.Equal(1, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));
        Assert.Equal(1, await IdentityDatabase.CountWorkspaceMembersAsync(connectionString, cancellationToken));
        using var intruderLogin = await api.LoginAsync(existing.Email, intruder.Password, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, intruderLogin, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-02")]
    public async Task Cadastros_concorrentes_com_o_mesmo_email_criam_um_so_usuario_e_os_demais_recebem_409()
    {
        const int Attempts = 6;
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var email = TestUser.New().Email;

        // Dado vários navegadores, cada um com seu token antiforgery
        var apis = Enumerable.Range(0, Attempts).Select(_ => new IdentityApi(factory)).ToList();
        try
        {
            var tokens = new List<AntiforgeryToken>();
            foreach (var api in apis)
            {
                tokens.Add(await api.GetAntiforgeryTokenAsync(cancellationToken));
            }

            // Quando todos se cadastram ao mesmo tempo com o mesmo e-mail
            var responses = await Task.WhenAll(apis.Select((api, index) => api.PostWithTokenAsync(
                IdentityApi.RegisterPath,
                IdentityApi.RegisterBody(new TestUser($"Vendedora {index}", email, TestUser.DefaultPassword)),
                tokens[index],
                cancellationToken)));
            try
            {
                // Então exatamente um 201; os demais 409 EMAIL_TAKEN; nunca 500
                var statuses = responses.Select(response => (int)response.StatusCode).ToList();
                Assert.DoesNotContain(statuses, status => status >= 500);
                Assert.Equal(1, statuses.Count(status => status == (int)HttpStatusCode.Created));
                foreach (var response in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
                {
                    await IdentityApi.AssertProblemAsync(HttpStatusCode.Conflict, "EMAIL_TAKEN", response, cancellationToken);
                }
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            foreach (var api in apis)
            {
                api.Dispose();
            }
        }

        // E exatamente um usuário, um workspace e um membro persistidos
        Assert.Equal(1, await IdentityDatabase.CountUsersWithEmailAsync(connectionString, email, cancellationToken));
        Assert.Equal(1, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));
        Assert.Equal(1, await IdentityDatabase.CountWorkspaceMembersAsync(connectionString, cancellationToken));
    }

    [Theory]
    [Trait("spec", "SPEC-0006:IT-02")]
    [InlineData("{\"name\": \"Marina\", \"email\": ")]
    [InlineData("isto não é json")]
    [InlineData("{\"name\": 1, \"email\": [], \"password\": {}}")]
    public async Task Cadastro_com_JSON_malformado_responde_400_problem_json_com_code_e_nada_e_persistido(string rawBody)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);

        // Dado um corpo que não é o JSON do contrato; quando POST /register com token antiforgery válido
        using var response = await api.PostRawJsonAsync(IdentityApi.RegisterPath, rawBody, cancellationToken);

        // Então 400 application/problem+json com code, sem sessão e sem cadastro
        await IdentityApi.AssertBadRequestProblemWithCodeAsync(response, cancellationToken);
        Assert.Empty(SetCookies.Session(response));
        Assert.Equal(0, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0006:IT-06")]
    public async Task Com_SignupOpen_false_POST_register_responde_404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado Features:SignupOpen=false
        await using var factory = IdentityHost.Create(connectionString, signupOpen: false);
        using var api = new IdentityApi(factory);

        // Quando POST /register com dados válidos e token antiforgery válido
        using var response = await api.RegisterAsync(TestUser.New(), cancellationToken);

        // Então 404 (rota inexistente para o público) e nada é persistido
        await IdentityApi.AssertStatusAsync(HttpStatusCode.NotFound, response, cancellationToken);
        Assert.Empty(SetCookies.Session(response));
        Assert.Equal(0, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));

        // Controle: a mesma requisição com o cadastro aberto é aceita (o 404 vem da flag, não de rota ausente)
        var openConnectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var openFactory = IdentityHost.Create(openConnectionString, signupOpen: true);
        using var openApi = new IdentityApi(openFactory);
        await openApi.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);
    }
}
