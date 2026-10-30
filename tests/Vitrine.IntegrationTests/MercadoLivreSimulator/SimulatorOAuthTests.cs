using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>SPEC-0024:IT-01 — fluxo OAuth completo do simulador por HTTP (página, decisão, troca do code e renovação).</summary>
public sealed class SimulatorOAuthTests
{
    private const string PageTitle = "Simulador do Mercado Livre — ambiente de teste";

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Pagina_de_autorizacao_se_identifica_como_simulador_e_lista_os_vendedores_com_autorizar_e_cancelar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Quando o navegador abre a página com parâmetros válidos
        var page = await client.GetAsync(SimulatorClient.AuthorizationUrl(PkcePair.New(), "estado-1"), cancellationToken: cancellationToken);

        // Então é HTML 200 com o rótulo do simulador, os vendedores (rótulo = nickname) e os dois botões
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Equal("text/html", page.ContentType);
        var html = WebUtility.HtmlDecode(page.Body);
        Assert.Contains(PageTitle, html, StringComparison.Ordinal);
        Assert.Contains("VENDEDOR_TESTE", html, StringComparison.Ordinal);
        Assert.Contains("VENDEDOR_AR", html, StringComparison.Ordinal);
        Assert.Contains(SimulatorHost.SellerTeste.ToString(CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
        Assert.Contains("Autorizar", html, StringComparison.Ordinal);
        Assert.Contains("Cancelar", html, StringComparison.Ordinal);
        Assert.Matches(@"name=""seller_id""", page.Body);
        Assert.Matches(@"name=""decision""[^>]*value=""authorize""|value=""authorize""[^>]*name=""decision""", page.Body);
        Assert.Matches(@"name=""decision""[^>]*value=""cancel""|value=""cancel""[^>]*name=""decision""", page.Body);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Autorizar_redireciona_com_code_e_o_mesmo_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        const string state = "estado_ABC-123.x~y";

        // Quando o vendedor autoriza
        var response = await client.DecideAsync(PkcePair.New(), state, SimulatorHost.SellerTeste, SimulatorClient.Authorize, cancellationToken: cancellationToken);

        // Então 302 para o redirect_uri com code e o mesmo state, sem error
        Assert.Equal(HttpStatusCode.Found, response.Status);
        Assert.NotNull(response.Location);
        Assert.StartsWith(SimulatorHost.RedirectUri + "?", response.Location!.ToString(), StringComparison.Ordinal);
        var query = SimulatorClient.QueryOf(response.Location);
        Assert.Equal(state, query["state"]);
        Assert.False(string.IsNullOrWhiteSpace(query.GetValueOrDefault("code")), "Redirecionamento sem code.");
        Assert.False(query.ContainsKey("error"));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Cancelar_redireciona_com_access_denied_e_o_mesmo_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Quando o vendedor cancela
        var response = await client.DecideAsync(PkcePair.New(), "estado-cancelado", SimulatorHost.SellerTeste, SimulatorClient.Cancel, cancellationToken: cancellationToken);

        // Então 302 para o redirect_uri com error=access_denied e o state, sem code
        Assert.Equal(HttpStatusCode.Found, response.Status);
        Assert.StartsWith(SimulatorHost.RedirectUri + "?", response.Location!.ToString(), StringComparison.Ordinal);
        var query = SimulatorClient.QueryOf(response.Location);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal("estado-cancelado", query["state"]);
        Assert.False(query.ContainsKey("code"));
    }

    [Theory]
    [Trait("spec", "SPEC-0024:IT-01")]
    [InlineData("client_id", "9999999999999999", SimulatorHost.RedirectUri, "S256")]
    [InlineData("redirect_uri", SimulatorHost.ClientId, "https://atacante.example/contas/mercado-livre/retorno", "S256")]
    [InlineData("code_challenge_method", SimulatorHost.ClientId, SimulatorHost.RedirectUri, "plain")]
    public async Task Pagina_com_parametro_invalido_responde_400_em_HTML_com_o_motivo(string reason, string clientId, string redirectUri, string method)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();

        // Quando a página recebe client_id desconhecido, redirect_uri fora da lista ou método diferente de S256
        var url = SimulatorClient.AuthorizationUrl(PkcePair.New(), "estado", clientId, redirectUri, method);
        var page = await client.GetAsync(url, cancellationToken: cancellationToken);

        // Então 400 HTML com o motivo (o parâmetro recusado), sem redirecionar
        Assert.Equal(HttpStatusCode.BadRequest, page.Status);
        Assert.Equal("text/html", page.ContentType);
        Assert.Null(page.Location);
        Assert.Contains(reason, page.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Troca_com_o_verifier_correto_devolve_tokens_no_formato_do_ML()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var pkce = PkcePair.New();
        var code = await client.AuthorizeCodeAsync(pkce, SimulatorHost.SellerTeste, cancellationToken: cancellationToken);

        // Quando a Api troca o code com o verifier correto
        var response = await client.ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);

        // Então 200 com access_token, token_type Bearer, expires_in padrão (21600), escopos, user_id e refresh_token
        Assert.True(response.Status == HttpStatusCode.OK, $"POST /oauth/token respondeu {(int)response.Status}: {response.Body}");
        Assert.Equal("application/json", response.ContentType);
        var json = response.Json;
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        Assert.Equal(21_600, json.GetProperty("expires_in").GetInt32());
        Assert.Equal("offline_access read write", json.GetProperty("scope").GetString());
        Assert.Equal(SimulatorHost.SellerTeste, json.GetProperty("user_id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("refresh_token").GetString()));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Troca_com_verifier_errado_responde_invalid_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var code = await client.AuthorizeCodeAsync(PkcePair.New(), SimulatorHost.SellerTeste, cancellationToken: cancellationToken);

        // Quando o verifier não corresponde ao challenge
        var response = await client.ExchangeAsync(code, PkcePair.New().Verifier, cancellationToken: cancellationToken);

        // Então 400 invalid_grant
        AssertOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Code_reutilizado_responde_invalid_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var pkce = PkcePair.New();
        var code = await client.AuthorizeCodeAsync(pkce, SimulatorHost.SellerTeste, cancellationToken: cancellationToken);
        var first = await client.ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.Status);

        // Quando o mesmo code é trocado de novo
        var second = await client.ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);

        // Então 400 invalid_grant (code de uso único)
        AssertOAuthError(second, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Troca_com_redirect_uri_diferente_responde_invalid_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var pkce = PkcePair.New();
        var code = await client.AuthorizeCodeAsync(pkce, SimulatorHost.SellerTeste, cancellationToken: cancellationToken);

        // Quando a troca informa outro redirect_uri (mesmo que também permitido)
        var response = await client.ExchangeAsync(code, pkce.Verifier, redirectUri: SimulatorHost.OtherRedirectUri, cancellationToken: cancellationToken);

        // Então 400 invalid_grant
        AssertOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Code_expirado_depois_de_10_minutos_responde_invalid_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var pkce = PkcePair.New();
        var code = await client.AuthorizeCodeAsync(pkce, SimulatorHost.SellerTeste, cancellationToken: cancellationToken);

        // Quando passam mais de 10 minutos
        host.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var response = await client.ExchangeAsync(code, pkce.Verifier, cancellationToken: cancellationToken);

        // Então 400 invalid_grant
        AssertOAuthError(response, HttpStatusCode.BadRequest, "invalid_grant");
    }

    [Theory]
    [Trait("spec", "SPEC-0024:IT-01")]
    [InlineData("9999999999999999", SimulatorHost.ClientSecret)]
    [InlineData(SimulatorHost.ClientId, "segredo-errado")]
    public async Task Troca_com_cliente_invalido_responde_401_invalid_client(string clientId, string clientSecret)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var pkce = PkcePair.New();
        var code = await client.AuthorizeCodeAsync(pkce, SimulatorHost.SellerTeste, cancellationToken: cancellationToken);

        // Quando client_id ou client_secret não confere
        var response = await client.ExchangeAsync(code, pkce.Verifier, clientId: clientId, clientSecret: clientSecret, cancellationToken: cancellationToken);

        // Então 401 invalid_client
        AssertOAuthError(response, HttpStatusCode.Unauthorized, "invalid_client");
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-01")]
    public async Task Renovacao_gira_o_refresh_token_e_o_antigo_responde_invalid_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = new SimulatorHost();
        using var client = host.NewClient();
        var tokens = await client.ConnectAsync(SimulatorHost.SellerTeste, cancellationToken);
        var oldRefresh = tokens.GetProperty("refresh_token").GetString()!;

        // Quando a Api renova com o último refresh token
        var renewed = await client.RefreshAsync(oldRefresh, cancellationToken);

        // Então 200 com access e refresh NOVOS
        Assert.True(renewed.Status == HttpStatusCode.OK, $"Renovação respondeu {(int)renewed.Status}: {renewed.Body}");
        var json = renewed.Json;
        var newRefresh = json.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(oldRefresh, newRefresh);
        Assert.NotEqual(tokens.GetProperty("access_token").GetString(), json.GetProperty("access_token").GetString());
        Assert.Equal(SimulatorHost.SellerTeste, json.GetProperty("user_id").GetInt64());

        // E o refresh anterior passa a dar invalid_grant, enquanto o novo vale
        AssertOAuthError(await client.RefreshAsync(oldRefresh, cancellationToken), HttpStatusCode.BadRequest, "invalid_grant");
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(newRefresh, cancellationToken)).Status);
    }

    /// <summary>Erro OAuth no formato do ML: <c>{ error, error_description, status, cause: [] }</c>.</summary>
    internal static void AssertOAuthError(SimulatorResponse response, HttpStatusCode status, string error)
    {
        Assert.True(response.Status == status, $"Esperava {(int)status} {error}; recebeu {(int)response.Status}: {response.Body}");
        var json = response.Json;
        Assert.Equal(error, json.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("error_description").ValueKind);
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("cause").ValueKind);
    }
}
