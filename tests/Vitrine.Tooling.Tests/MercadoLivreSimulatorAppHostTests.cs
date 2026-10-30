using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Vitrine.Tooling.Tests.Support;
using TempDirectory = Vitrine.Tooling.Tests.Support.TempDirectory;

namespace Vitrine.Tooling.Tests;

/// <summary>
/// SPEC-0024:IT-04 — o AppHost sobe o simulador do Mercado Livre só em modo run (recurso <c>ml-simulator</c>, Api e
/// simulador apontados um para o outro), nunca no <c>aspire publish</c>, e a fixture do E2E informa
/// <c>ML_SIMULATOR_URL</c> ao Playwright.
/// </summary>
public sealed class MercadoLivreSimulatorAppHostTests
{
    private const string Simulator = "ml-simulator";
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromMinutes(8);

    [Fact]
    [Trait("spec", "SPEC-0024:IT-04")]
    public async Task Em_modo_run_existe_ml_simulator_e_a_Api_e_o_simulador_apontam_um_para_o_outro()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o AppHost em modo run (o do `aspire run` e do E2E)
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Vitrine_AppHost>(cancellationToken);
        Assert.True(builder.ExecutionContext.IsRunMode);
        var resources = builder.Resources.ToDictionary(r => r.Name, StringComparer.Ordinal);

        // Então existe o recurso ml-simulator, com health check
        Assert.True(
            resources.TryGetValue(Simulator, out var simulator),
            $"Recurso '{Simulator}' ausente em modo run. Recursos: [{string.Join(", ", resources.Keys.Order())}]");
        Assert.True(simulator!.Annotations.OfType<HealthCheckAnnotation>().Any(), "ml-simulator sem health check (GET /health).");

        await using var app = await builder.BuildAsync(cancellationToken);

        // E a Api recebe MercadoLivre__AuthorizationBaseUrl/__ApiBaseUrl apontando para o simulador, credenciais sintéticas
        // e o RedirectUri da SPA
        var api = await EnvironmentOf(resources["api"], builder, cancellationToken);
        Assert.Contains($"{{{Simulator}.bindings.", Describe(api, "MercadoLivre__AuthorizationBaseUrl"), StringComparison.Ordinal);
        Assert.Contains($"{{{Simulator}.bindings.", Describe(api, "MercadoLivre__ApiBaseUrl"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(Describe(api, "MercadoLivre__ClientId")));
        Assert.False(string.IsNullOrWhiteSpace(Describe(api, "MercadoLivre__ClientSecret")));
        var redirect = Describe(api, "MercadoLivre__RedirectUri");
        Assert.Contains("{web.bindings.", redirect, StringComparison.Ordinal);
        Assert.EndsWith("/contas/mercado-livre/retorno", redirect, StringComparison.Ordinal);

        // E o simulador usa as mesmas credenciais e notifica a Api
        var sim = await EnvironmentOf(simulator, builder, cancellationToken);
        Assert.Equal(Describe(api, "MercadoLivre__ClientId"), Describe(sim, "Simulator__ClientId"));
        Assert.Equal(Describe(api, "MercadoLivre__ClientSecret"), Describe(sim, "Simulator__ClientSecret"));
        var notificationUrl = Describe(sim, "Simulator__NotificationUrl");
        Assert.Contains("{api.bindings.", notificationUrl, StringComparison.Ordinal);
        Assert.EndsWith("/api/v1/mercadolivre/notifications", notificationUrl, StringComparison.Ordinal);
        Assert.Contains(
            sim.Where(kv => kv.Key.StartsWith("Simulator__AllowedRedirectUris__", StringComparison.Ordinal)).Select(kv => Describe(kv.Value)),
            value => value == redirect);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-04")]
    public async Task Em_modo_publish_o_modelo_nao_tem_ml_simulator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var output = new TempDirectory();

        // Dado o AppHost em modo publish
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Vitrine_AppHost>(
            ["--operation", "publish", "--step", "publish", "--output-path", output.Path], cancellationToken);
        Assert.True(builder.ExecutionContext.IsPublishMode, "Os argumentos não colocaram o AppHost em modo publish.");

        // Então o recurso não existe (e a Api continua lá)
        var names = builder.Resources.Select(r => r.Name).Order().ToList();
        Assert.Contains("api", names);
        Assert.DoesNotContain(Simulator, names);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-04")]
    public async Task Compose_gerado_pelo_aspire_publish_nao_tem_o_simulador()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var output = new TempDirectory();

        // Quando o compose-bundle é gerado como no CI (equivalente ao `aspire publish`)
        var result = await ProcessRunner.RunAsync(
            "dotnet",
            ["run", "--project", RepoRoot.Combine("src", "AppHost", "Vitrine.AppHost.csproj"), "--",
             "--operation", "publish", "--step", "publish", "--output-path", output.Path],
            RepoRoot.Path,
            PublishTimeout,
            cancellationToken: cancellationToken);
        Assert.True(result.ExitCode == 0, $"aspire publish falhou:\n{result.Output}");

        // Então o docker-compose.yaml e o .env gerados não mencionam o simulador
        var compose = Path.Combine(output.Path, "docker-compose.yaml");
        Assert.True(File.Exists(compose), $"docker-compose.yaml não gerado em {output.Path}: [{string.Join(", ", Directory.EnumerateFiles(output.Path).Select(Path.GetFileName))}]");
        foreach (var file in Directory.EnumerateFiles(output.Path, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            Assert.DoesNotContain(Simulator, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MercadoLivreSimulator", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Simulator__", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-04")]
    public async Task Fixture_do_E2E_escreve_ML_SIMULATOR_URL_antes_do_BASE_URL_e_o_simulador_responde()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(FixtureTimeout);
        var cancellationToken = cts.Token;
        var supportDir = RepoRoot.Combine("web", "e2e", "support");

        // Dado o lado TypeScript da fixture, que repassa a variável ao Playwright
        var typescript = await File.ReadAllTextAsync(Path.Combine(supportDir, "app-host-fixture.ts"), cancellationToken);
        Assert.Contains("ML_SIMULATOR_URL", typescript, StringComparison.Ordinal);

        // E a fixture do E2E (AppHostFixture.cs) iniciada como o Playwright a inicia
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = supportDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "run", "--file", Path.Combine(supportDir, "AppHostFixture.cs") })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var fixture = Process.Start(startInfo)!;
        var stderr = fixture.StandardError.ReadToEndAsync(cancellationToken);
        var lines = new List<string>();
        string? simulatorUrl = null;
        string? baseUrl = null;
        try
        {
            // Quando a aplicação fica pronta (linha BASE_URL=..., o sinal que o Playwright espera)
            while (baseUrl is null)
            {
                var line = await fixture.StandardOutput.ReadLineAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"AppHostFixture terminou sem BASE_URL. stdout:\n{string.Join('\n', lines)}\nstderr:\n{await stderr}");
                lines.Add(line);
                if (line.StartsWith("ML_SIMULATOR_URL=", StringComparison.Ordinal))
                {
                    simulatorUrl = line["ML_SIMULATOR_URL=".Length..];
                }
                else if (line.StartsWith("BASE_URL=", StringComparison.Ordinal))
                {
                    baseUrl = line["BASE_URL=".Length..];
                }
            }

            // Então ML_SIMULATOR_URL=<url> veio antes e o simulador responde GET /health com 200
            Assert.True(simulatorUrl is not null, $"AppHostFixture escreveu BASE_URL sem ML_SIMULATOR_URL antes. stdout:\n{string.Join('\n', lines)}");
            Assert.True(Uri.TryCreate(simulatorUrl, UriKind.Absolute, out var simulator), $"ML_SIMULATOR_URL inválida: '{simulatorUrl}'");
            using var http = new HttpClient { BaseAddress = simulator };
            using var health = await http.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            // Entrada padrão fechada derruba a aplicação (contrato da fixture)
            fixture.StandardInput.Close();
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await fixture.WaitForExitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                fixture.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>Variáveis de ambiente do recurso, avaliando os callbacks do modelo (sem iniciar nada).</summary>
    private static async Task<Dictionary<string, object>> EnvironmentOf(
        IResource resource, IDistributedApplicationTestingBuilder builder, CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, variables, cancellationToken);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return variables;
    }

    private static string Describe(Dictionary<string, object> variables, string name)
    {
        Assert.True(
            variables.TryGetValue(name, out var value),
            $"Variável '{name}' ausente. Variáveis: [{string.Join(", ", variables.Keys.Order(StringComparer.Ordinal))}]");
        return Describe(value);
    }

    /// <summary>Texto do valor: a expressão do manifesto (ex.: <c>{ml-simulator.bindings.http.url}</c>) ou o literal.</summary>
    private static string Describe(object? value) => value switch
    {
        IManifestExpressionProvider expression => expression.ValueExpression,
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}
