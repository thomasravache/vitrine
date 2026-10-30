using Microsoft.Extensions.Options;
using Vitrine.MercadoLivre.Infrastructure;
using Vitrine.MercadoLivre.Infrastructure.Api;

namespace Vitrine.UnitTests.MercadoLivre;

/// <summary>O token só vai no cabeçalho <c>Authorization</c> de chamadas ao <c>ApiBaseUrl</c> (SPEC-0021, ADR-0006).</summary>
public sealed class MercadoLivreAuthenticationHandlerTests
{
    private const string Token = "APP_USR-access-teste-unitario";

    [Fact]
    [Trait("spec", "SPEC-0021:UT-03")]
    public async Task Requisicao_ao_ApiBaseUrl_recebe_Bearer_da_conta()
    {
        // Dado o handler com o ApiBaseUrl oficial e uma conta com token
        var accountId = Guid.NewGuid();
        var (invoker, inner, tokens) = Build(MercadoLivreOptions.OfficialApiBaseUrl);

        // Quando a requisição vai para a API do ML
        using var request = Request("https://api.mercadolibre.com/items/bulk?ids=MLB1", accountId);
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        // Então leva Authorization: Bearer <token> da conta
        var sent = Assert.Single(inner.Requests);
        Assert.Equal("Bearer", sent.Headers.Authorization?.Scheme);
        Assert.Equal(Token, sent.Headers.Authorization?.Parameter);
        Assert.Equal([accountId], tokens.Requested);
    }

    [Theory]
    [Trait("spec", "SPEC-0021:UT-03")]
    [InlineData(MercadoLivreOptions.OfficialApiBaseUrl, "https://evil.example.com/users/me")]
    [InlineData(MercadoLivreOptions.OfficialApiBaseUrl, "https://api.mercadolibre.com.evil.example/items/bulk")]
    [InlineData(MercadoLivreOptions.OfficialApiBaseUrl, "https://auth.mercadolivre.com.br/authorization")]
    [InlineData("https://ml-simulador.vitrine.test", "https://api.mercadolibre.com/items/bulk")]
    public async Task Requisicao_para_outro_host_nao_recebe_o_token(string apiBaseUrl, string url)
    {
        // Dado o handler configurado com apiBaseUrl
        var (invoker, inner, tokens) = Build(apiBaseUrl);

        // Quando a requisição vai para um host diferente do ApiBaseUrl
        using var request = Request(url, Guid.NewGuid());
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        // Então segue sem Authorization e sem pedir o token da conta
        var sent = Assert.Single(inner.Requests);
        Assert.Null(sent.Headers.Authorization);
        Assert.Empty(tokens.Requested);
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-03")]
    public async Task ApiBaseUrl_configurado_simulador_recebe_o_token()
    {
        // Dado o ApiBaseUrl do simulador (SPEC-0024)
        var accountId = Guid.NewGuid();
        var (invoker, inner, _) = Build("https://ml-simulador.vitrine.test");

        using var request = Request("https://ml-simulador.vitrine.test/users/123/items/search?status=active", accountId);
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(Token, Assert.Single(inner.Requests).Headers.Authorization?.Parameter);
    }

    private static HttpRequestMessage Request(string url, Guid accountId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Options.Set(MercadoLivreAuthenticationHandler.AccountId, accountId);
        return request;
    }

    private static (HttpMessageInvoker Invoker, RecordingHandler Inner, FakeTokens Tokens) Build(string apiBaseUrl)
    {
        var tokens = new FakeTokens();
        var inner = new RecordingHandler();
#pragma warning disable CA2000 // O invoker descarta o handler (disposeHandler: true).
        var handler = new MercadoLivreAuthenticationHandler(tokens, Options.Create(new MercadoLivreOptions { ApiBaseUrl = apiBaseUrl }))
        {
            InnerHandler = inner,
        };
#pragma warning restore CA2000
        return (new HttpMessageInvoker(handler, disposeHandler: true), inner, tokens);
    }

    private sealed class FakeTokens : IMercadoLivreAccessTokenProvider
    {
        public List<Guid> Requested { get; } = [];

        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken)
        {
            Requested.Add(accountId);
            return Task.FromResult(Token);
        }

        public Task<string> RenewAfterRejectionAsync(Guid accountId, string rejectedAccessToken, CancellationToken cancellationToken)
        {
            Requested.Add(accountId);
            return Task.FromResult(Token);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
