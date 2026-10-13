using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Vitrine.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class ExternalHttpTests(PostgresFixture postgres)
{
    private const string ExternalClientName = "servico-externo";

    // .invalid nunca resolve (RFC 6761): se a chamada saísse para a rede, falharia.
    private static readonly Uri ExternalBaseAddress = new("https://api.servico-externo.invalid");

    [Fact]
    [Trait("spec", "SPEC-0003:IT-03")]
    public async Task Chamada_da_Api_a_cliente_HTTP_externo_nomeado_e_atendida_pelo_WireMock_sem_sair_para_a_internet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado a ApiFactory com o WireMock substituindo o cliente HTTP externo nomeado
        await using var factory = new ApiFactory(connectionString)
            .WithServices(services =>
            {
                services.AddHttpClient(ExternalClientName, client => client.BaseAddress = ExternalBaseAddress);
                services.AddSingleton<IStartupFilter, ExternalCallEndpoint>();
            })
            .RedirectToWireMock(ExternalClientName);

        factory.ExternalServices
            .Given(Request.Create().WithPath("/v1/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("pong do wiremock"));

        // Quando a API faz a chamada externa
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(ExternalCallEndpoint.Path, UriKind.Relative), cancellationToken);

        // Então o WireMock atende e registra a requisição (nenhuma conexão saiu para a internet)
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"A chamada externa falhou ({(int)response.StatusCode}): {body}");
        Assert.Equal("pong do wiremock", body);
        var entry = Assert.Single(factory.ExternalServices.LogEntries);
        Assert.Equal("/v1/ping", entry.RequestMessage?.Path);
    }

    /// <summary>Endpoint de teste dentro da Api que chama o cliente HTTP externo nomeado.</summary>
    private sealed class ExternalCallEndpoint : IStartupFilter
    {
        public const string Path = "/__test/external-call";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, endpoint => endpoint.Run(async context =>
            {
                var client = context.RequestServices
                    .GetRequiredService<IHttpClientFactory>()
                    .CreateClient(ExternalClientName);
                var body = await client.GetStringAsync(new Uri("/v1/ping", UriKind.Relative), context.RequestAborted);
                await context.Response.WriteAsync(body, context.RequestAborted);
            }));
            next(app);
        };
    }
}
