using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Vitrine.Tooling.Tests;

public sealed class LocalEnvironmentTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan WebReadyTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    [Trait("spec", "SPEC-0002:IT-01")]
    public async Task AppHost_iniciado_sobe_api_saudavel_e_web_servindo_a_spa()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(StartupTimeout);
        var cancellationToken = cts.Token;

        // Dado Docker disponível e o AppHost
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Vitrine_AppHost>(cancellationToken);

        var names = builder.Resources.Select(r => r.Name).Order().ToList();
        Assert.True(names.Contains("api"), $"Recurso 'api' ausente no AppHost. Recursos: [{string.Join(", ", names)}]");
        Assert.True(names.Contains("web"), $"Recurso 'web' ausente no AppHost. Recursos: [{string.Join(", ", names)}]");

        // Quando o AppHost é iniciado via Aspire.Hosting.Testing
        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        // Então GET /health/live da api retorna 200
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);
        using var apiClient = app.CreateHttpClient("api");
        using var health = await apiClient.GetAsync("/health/live", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // E GET / da web retorna HTML com <div id="root">
        await app.ResourceNotifications.WaitForResourceAsync("web", KnownResourceStates.Running, cancellationToken);
        using var webClient = app.CreateHttpClient("web");
        var html = await GetWhenReadyAsync(webClient, "/", cancellationToken);
        Assert.Contains("<div id=\"root\">", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// O processo do Vite fica "Running" antes de aceitar conexões; aguarda o servidor responder
    /// (prontidão, não repetição de asserção) até <see cref="WebReadyTimeout"/>.
    /// </summary>
    private static async Task<string> GetWhenReadyAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + WebReadyTimeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(path, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }

                last = new HttpRequestException($"GET {path} retornou {(int)response.StatusCode}.");
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"'web' não respondeu GET {path} com sucesso em {WebReadyTimeout}.", last);
    }
}
