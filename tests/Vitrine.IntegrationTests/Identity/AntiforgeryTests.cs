using System.Net;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity;

[Collection(PostgresTestGroup.Name)]
public sealed class AntiforgeryTests(PostgresFixture postgres)
{
    /// <summary>Como o POST chega sem token antiforgery válido.</summary>
    public enum MissingToken
    {
        /// <summary>Navegador que nunca chamou GET /antiforgery: sem cookie e sem cabeçalho.</summary>
        NoCookieNoHeader,

        /// <summary>Com o cookie antiforgery, mas sem o cabeçalho com o token.</summary>
        CookieWithoutHeader,

        /// <summary>Com o cookie e um token adulterado no cabeçalho.</summary>
        TamperedHeader,
    }

    [Theory]
    [Trait("spec", "SPEC-0006:IT-05")]
    [InlineData(MissingToken.NoCookieNoHeader)]
    [InlineData(MissingToken.CookieWithoutHeader)]
    [InlineData(MissingToken.TamperedHeader)]
    public async Task Register_sem_token_antiforgery_valido_responde_400_e_nao_cadastra(MissingToken variant)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);

        // Dado POST /register com dados válidos e sem token antiforgery válido; quando enviado
        var token = await TokenFor(variant, api, cancellationToken);
        using var response = await api.PostWithTokenAsync(IdentityApi.RegisterPath, IdentityApi.RegisterBody(TestUser.New()), token, cancellationToken);

        // Então 400, sem sessão e sem cadastro
        await IdentityApi.AssertStatusAsync(HttpStatusCode.BadRequest, response, cancellationToken);
        Assert.Empty(SetCookies.Session(response));
        Assert.Equal(0, await IdentityDatabase.CountWorkspacesAsync(connectionString, cancellationToken));
    }

    [Theory]
    [Trait("spec", "SPEC-0006:IT-05")]
    [InlineData(MissingToken.NoCookieNoHeader)]
    [InlineData(MissingToken.CookieWithoutHeader)]
    [InlineData(MissingToken.TamperedHeader)]
    public async Task Login_sem_token_antiforgery_valido_responde_400_e_nao_abre_sessao(MissingToken variant)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var user = TestUser.New();
        using (var owner = new IdentityApi(factory))
        {
            await owner.RegisterSuccessfullyAsync(user, cancellationToken);
        }

        using var api = new IdentityApi(factory);

        // Dado POST /login com a senha correta e sem token antiforgery válido; quando enviado
        var token = await TokenFor(variant, api, cancellationToken);
        using var response = await api.PostWithTokenAsync(IdentityApi.LoginPath, new { email = user.Email, password = user.Password }, token, cancellationToken);

        // Então 400 e nenhuma sessão aberta
        await IdentityApi.AssertStatusAsync(HttpStatusCode.BadRequest, response, cancellationToken);
        Assert.Empty(SetCookies.Session(response));
        using var me = await api.GetMeAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.Unauthorized, me, cancellationToken);
    }

    [Theory]
    [Trait("spec", "SPEC-0006:IT-05")]
    [InlineData(MissingToken.CookieWithoutHeader)]
    [InlineData(MissingToken.TamperedHeader)]
    public async Task Logout_sem_token_antiforgery_valido_responde_400_e_mantem_a_sessao(MissingToken variant)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        using var api = new IdentityApi(factory);
        await api.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);

        // Dado uma sessão ativa; quando POST /logout sem token antiforgery válido
        var token = await TokenFor(variant, api, cancellationToken);
        using var response = await api.PostWithTokenAsync(IdentityApi.LogoutPath, null, token, cancellationToken);

        // Então 400 e a sessão continua ativa
        await IdentityApi.AssertStatusAsync(HttpStatusCode.BadRequest, response, cancellationToken);
        using var me = await api.GetMeAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);
    }

    private static async Task<AntiforgeryToken?> TokenFor(MissingToken variant, IdentityApi api, CancellationToken cancellationToken) =>
        variant switch
        {
            MissingToken.NoCookieNoHeader => null,
            MissingToken.CookieWithoutHeader => await DiscardToken(api, cancellationToken),
            MissingToken.TamperedHeader => (await api.GetAntiforgeryTokenAsync(cancellationToken)).Tampered(),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };

    // Obtém o cookie antiforgery no cliente, mas não envia o token.
    private static async Task<AntiforgeryToken?> DiscardToken(IdentityApi api, CancellationToken cancellationToken)
    {
        _ = await api.GetAntiforgeryTokenAsync(cancellationToken);
        return null;
    }
}
