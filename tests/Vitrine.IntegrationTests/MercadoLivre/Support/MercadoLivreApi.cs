using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Navegador de um usuário logado contra os endpoints do módulo MercadoLivre (contrato da SPEC-0020), com o fluxo
/// antiforgery do contrato (token em <c>X-XSRF-TOKEN</c> nos POST/DELETE).
/// </summary>
internal sealed class MercadoLivreApi : IDisposable
{
    public const string ConnectionsPath = "/api/v1/mercadolivre/connections";
    public const string CompletePath = "/api/v1/mercadolivre/connections/complete";
    public const string AccountsPath = "/api/v1/mercadolivre/accounts";

    public MercadoLivreApi(ApiFactory factory)
    {
        Http = new IdentityApi(factory);
    }

    /// <summary>Cliente HTTP do navegador (sessão e antiforgery).</summary>
    public IdentityApi Http { get; }

    /// <summary>Conta e workspace do usuário depois de <see cref="SignUpAsync"/>.</summary>
    public RegisteredAccount Account { get; private set; } = new(Guid.Empty, Guid.Empty);

    public static string AccountPath(Guid accountId) => $"{AccountsPath}/{accountId:D}";

    /// <summary>Cadastra um usuário novo (Owner do próprio workspace); a sessão fica neste navegador.</summary>
    public async Task<RegisteredAccount> SignUpAsync(CancellationToken cancellationToken)
    {
        Account = await Http.RegisterSuccessfullyAsync(TestUser.New(), cancellationToken);
        return Account;
    }

    /// <summary><c>POST /connections</c> com corpo <c>{}</c>.</summary>
    public Task<HttpResponseMessage> StartAsync(CancellationToken cancellationToken) =>
        Http.PostAsync(ConnectionsPath, new { }, cancellationToken);

    /// <summary><c>POST /connections</c> exigindo 200 com <c>authorizationUrl</c>.</summary>
    public async Task<StartedConnection> StartSuccessfullyAsync(CancellationToken cancellationToken)
    {
        using var response = await StartAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, response, cancellationToken);
        var json = await IdentityApi.ReadJsonAsync(response, cancellationToken);
        Assert.True(
            json.TryGetProperty("authorizationUrl", out var url) && url.ValueKind == JsonValueKind.String,
            $"POST {ConnectionsPath} sem authorizationUrl: {json}");
        return StartedConnection.From(new Uri(url.GetString()!));
    }

    /// <summary><c>POST /connections/complete</c> com <c>{ code, state }</c>.</summary>
    public Task<HttpResponseMessage> CompleteAsync(string code, string state, CancellationToken cancellationToken) =>
        Http.PostAsync(CompletePath, new { code, state }, cancellationToken);

    public Task<HttpResponseMessage> ListAsync(CancellationToken cancellationToken) =>
        Http.GetAsync(AccountsPath, cancellationToken);

    public Task<HttpResponseMessage> DisconnectAsync(Guid accountId, CancellationToken cancellationToken) =>
        Http.DeleteAsync(AccountPath(accountId), cancellationToken);

    /// <summary>
    /// Conexão completa contra o ML simulado: inicia, "volta" do ML com um code novo e o state recebido, e conclui.
    /// Exige <paramref name="expected"/> (201 conta nova, 200 reconexão) e devolve a conta.
    /// </summary>
    public async Task<ConnectedAccount> ConnectAsync(
        MercadoLivreFake fake, MlSeller seller, MlTokens tokens, CancellationToken cancellationToken, HttpStatusCode expected = HttpStatusCode.Created)
    {
        ArgumentNullException.ThrowIfNull(fake);
        fake.Reset();
        fake.Accepts(seller, tokens);
        var started = await StartSuccessfullyAsync(cancellationToken);
        using var response = await CompleteAsync(NewCode(), started.State, cancellationToken);
        await IdentityApi.AssertStatusAsync(expected, response, cancellationToken);
        return await ConnectedAccount.ReadAsync(response, cancellationToken);
    }

    /// <summary>Lista de contas (exige 200 e um array JSON).</summary>
    public async Task<List<JsonElement>> ListSuccessfullyAsync(CancellationToken cancellationToken)
    {
        using var response = await ListAsync(cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, response, cancellationToken);
        var json = await IdentityApi.ReadJsonAsync(response, cancellationToken);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        return [.. json.EnumerateArray()];
    }

    /// <summary>Code de autorização fictício, como o ML devolve na URL de retorno.</summary>
    public static string NewCode() => $"TG-code-teste-{Guid.NewGuid():N}";

    public void Dispose() => Http.Dispose();
}

/// <summary>URL de autorização devolvida por <c>POST /connections</c> e os parâmetros dela.</summary>
internal sealed record StartedConnection(Uri AuthorizationUrl, IReadOnlyDictionary<string, string> Query)
{
    public string State => Query.TryGetValue("state", out var state) ? state : string.Empty;

    public string CodeChallenge => Query.TryGetValue("code_challenge", out var challenge) ? challenge : string.Empty;

    public static StartedConnection From(Uri authorizationUrl)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        var query = QueryHelpers.ParseQuery(authorizationUrl.Query)
            .ToDictionary(parameter => parameter.Key, parameter => parameter.Value.ToString(), StringComparer.Ordinal);
        return new StartedConnection(authorizationUrl, query);
    }
}

/// <summary>Resposta 201/200 de <c>POST /connections/complete</c>.</summary>
internal sealed record ConnectedAccount(Guid AccountId, long MlUserId, string Nickname, string Status)
{
    public static async Task<ConnectedAccount> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await IdentityApi.ReadJsonAsync(response, cancellationToken);
        Assert.True(json.TryGetProperty("accountId", out var accountId) && accountId.TryGetGuid(out _), $"Resposta sem accountId UUID: {json}");
        Assert.True(
            json.TryGetProperty("mlUserId", out var mlUserId) && mlUserId.ValueKind == JsonValueKind.Number && mlUserId.TryGetInt64(out _),
            $"Resposta sem mlUserId numérico (int64): {json}");
        return new ConnectedAccount(
            accountId.GetGuid(),
            mlUserId.GetInt64(),
            json.GetProperty("nickname").GetString() ?? string.Empty,
            json.GetProperty("status").GetString() ?? string.Empty);
    }
}
