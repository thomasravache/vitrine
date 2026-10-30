using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Identity.Support;

/// <summary>
/// Cliente HTTP de um "navegador" contra a Api real em memória: HTTPS (cookies <c>Secure</c> voltam ao servidor),
/// cookies mantidos entre requisições, sem seguir redirecionamentos, e o fluxo antiforgery do contrato
/// (GET /api/v1/auth/antiforgery e depois o POST com o token).
/// </summary>
internal sealed class IdentityApi : IDisposable
{
    public const string RegisterPath = "/api/v1/auth/register";
    public const string LoginPath = "/api/v1/auth/login";
    public const string LogoutPath = "/api/v1/auth/logout";
    public const string AntiforgeryPath = "/api/v1/auth/antiforgery";
    public const string MePath = "/api/v1/me";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _client;

    public IdentityApi(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
    }

    /// <summary>GET /api/v1/auth/antiforgery: 204 com o token.</summary>
    public async Task<AntiforgeryToken> GetAntiforgeryTokenAsync(CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        using var response = await _client.GetAsync(new Uri(AntiforgeryPath, UriKind.Relative), timeout.Token);
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"GET {AntiforgeryPath} deveria responder 204; respondeu {(int)response.StatusCode} {response.StatusCode}.");
        return AntiforgeryToken.From(response);
    }

    /// <summary>POST com token antiforgery recém-obtido (o token acompanha a identidade atual da sessão).</summary>
    public async Task<HttpResponseMessage> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        var token = await GetAntiforgeryTokenAsync(cancellationToken);
        return await PostWithTokenAsync(path, body, token, cancellationToken);
    }

    /// <summary>POST com o token informado (ou sem token, quando <paramref name="token"/> é nulo).</summary>
    public async Task<HttpResponseMessage> PostWithTokenAsync(string path, object? body, AntiforgeryToken? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        if (token is not null)
        {
            request.Headers.Add(token.HeaderName, token.Value);
        }

        using var timeout = Timeout(cancellationToken);
        return await _client.SendAsync(request, timeout.Token);
    }

    /// <summary>DELETE com token antiforgery recém-obtido.</summary>
    public async Task<HttpResponseMessage> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var token = await GetAntiforgeryTokenAsync(cancellationToken);
        return await DeleteWithTokenAsync(path, token, cancellationToken);
    }

    /// <summary>DELETE com o token informado (ou sem token, quando <paramref name="token"/> é nulo).</summary>
    public async Task<HttpResponseMessage> DeleteWithTokenAsync(string path, AntiforgeryToken? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Add(token.HeaderName, token.Value);
        }

        using var timeout = Timeout(cancellationToken);
        return await _client.SendAsync(request, timeout.Token);
    }

    /// <summary>POST com token antiforgery válido e corpo bruto (ex.: JSON malformado).</summary>
    public async Task<HttpResponseMessage> PostRawJsonAsync(string path, string rawBody, CancellationToken cancellationToken)
    {
        var token = await GetAntiforgeryTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(token.HeaderName, token.Value);

        using var timeout = Timeout(cancellationToken);
        return await _client.SendAsync(request, timeout.Token);
    }

    /// <summary>GET relativo à Api (sem antiforgery).</summary>
    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        return await _client.GetAsync(new Uri(path, UriKind.Relative), timeout.Token);
    }

    /// <summary>Erro 400 em application/problem+json com um campo <c>code</c> não vazio; devolve o corpo.</summary>
    public static async Task<JsonElement> AssertBadRequestProblemWithCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await AssertStatusAsync(HttpStatusCode.BadRequest, response, cancellationToken);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response, cancellationToken);
        Assert.True(
            problem.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString()),
            $"Problem sem \"code\": {problem}");
        return problem;
    }

    public Task<HttpResponseMessage> RegisterAsync(TestUser user, CancellationToken cancellationToken) =>
        PostAsync(RegisterPath, RegisterBody(user), cancellationToken);

    /// <summary>Cadastra e exige 201 com <c>{ userId, workspaceId }</c>; a sessão fica neste cliente.</summary>
    public async Task<RegisteredAccount> RegisterSuccessfullyAsync(TestUser user, CancellationToken cancellationToken)
    {
        using var response = await RegisterAsync(user, cancellationToken);
        return await ReadRegisteredAccountAsync(response, cancellationToken);
    }

    public Task<HttpResponseMessage> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        PostAsync(LoginPath, new { email, password }, cancellationToken);

    public Task<HttpResponseMessage> LogoutAsync(CancellationToken cancellationToken) =>
        PostAsync(LogoutPath, null, cancellationToken);

    public async Task<HttpResponseMessage> GetMeAsync(CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        return await _client.GetAsync(new Uri(MePath, UriKind.Relative), timeout.Token);
    }

    public static object RegisterBody(TestUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new { name = user.Name, email = user.Email, password = user.Password };
    }

    public static async Task<RegisteredAccount> ReadRegisteredAccountAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await AssertStatusAsync(HttpStatusCode.Created, response, cancellationToken);
        var json = await ReadJsonAsync(response, cancellationToken);
        return new RegisteredAccount(
            Guid.Parse(json.GetProperty("userId").GetString()!),
            Guid.Parse(json.GetProperty("workspaceId").GetString()!));
    }

    /// <summary>Exige o status, mostrando o corpo na falha.</summary>
    public static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode != expected)
        {
            var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Fail(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: esperado "
                + $"{(int)expected} {expected}, recebido {(int)response.StatusCode} {response.StatusCode}. Corpo: {body}");
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>Erro no formato application/problem+json com o campo <c>code</c> esperado; devolve o corpo.</summary>
    public static async Task<JsonElement> AssertProblemAsync(
        HttpStatusCode status, string code, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await AssertStatusAsync(status, response, cancellationToken);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response, cancellationToken);
        Assert.True(
            problem.TryGetProperty("code", out var actualCode) && actualCode.GetString() == code,
            $"Problem sem code \"{code}\": {problem}");
        return problem;
    }

    public void Dispose() => _client.Dispose();

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return timeout;
    }
}
