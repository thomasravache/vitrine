using System.Text.Json;
using System.Text.RegularExpressions;
using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

/// <summary>
/// SPEC-0024:IT-03 (ADR-0016, Confirmação) — o simulador do Mercado Livre só existe no ambiente local prod-like: serviço
/// <c>ml-simulator</c> só no perfil <c>ml-simulator</c>, perfil e diretório de sites extras do Caddy ligados só pelo
/// <c>deploy/local/.env.example</c>, <c>deploy/Caddyfile</c> sem menção ao simulador e diretório padrão de sites vazio.
/// O Compose é lido por <c>docker compose config</c> (os mesmos arquivos do <c>deploy/deploy.sh</c>).
/// </summary>
public sealed partial class MercadoLivreSimulatorDeployTests
{
    private const string Service = "ml-simulator";
    private const string SimulatorHost = "ml-simulador.vitrine.localhost";
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] ScannedDirectories = ["deploy", ".github", "infra"];

    [Fact]
    [Trait("spec", "SPEC-0024:IT-03")]
    public async Task Servico_ml_simulator_so_existe_com_o_perfil_ml_simulator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração padrão (staging/produção: sem COMPOSE_PROFILES do simulador)
        var standard = await ComposeConfigAsync(EnvMode.Default, profiles: null, cancellationToken);

        // Então o serviço não existe
        Assert.False(standard.GetProperty("services").TryGetProperty(Service, out _), "ml-simulator ativo sem o perfil ml-simulator.");

        // E o Compose base de produção (deploy/compose.yaml, sem o override) não o declara nem com o perfil ligado
        var baseOnly = await ComposeConfigAsync(EnvMode.Default, profiles: "ml-simulator", cancellationToken, overrideFile: false);
        Assert.False(baseOnly.GetProperty("services").TryGetProperty(Service, out _), "deploy/compose.yaml declara ml-simulator.");

        // Quando o perfil ml-simulator é ligado
        var withProfile = await ComposeConfigAsync(EnvMode.Default, profiles: "ml-simulator", cancellationToken);

        // Então o serviço existe, exatamente com profiles: ["ml-simulator"]
        Assert.True(withProfile.GetProperty("services").TryGetProperty(Service, out var simulator), "ml-simulator ausente com o perfil ml-simulator.");
        var profiles = simulator.TryGetProperty("profiles", out var p) ? p.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        Assert.Equal(["ml-simulator"], profiles);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-03")]
    public async Task Ambiente_local_liga_o_simulador_e_aponta_a_Api_e_o_simulador_um_para_o_outro()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o deploy/local/.env.example como está
        var local = await ComposeConfigAsync(EnvMode.Local, profiles: null, cancellationToken);
        var services = local.GetProperty("services");

        // Então o perfil ml-simulator está ligado
        Assert.True(services.TryGetProperty(Service, out var simulator), "deploy/local/.env.example não liga o perfil ml-simulator.");

        // E a Api local aponta para o simulador (contrato da seção 6)
        var api = Environment(services.GetProperty("api"));
        Assert.Equal($"https://{SimulatorHost}", api.GetValueOrDefault("MercadoLivre__AuthorizationBaseUrl")?.TrimEnd('/'));
        Assert.Equal("http://ml-simulator:8080", api.GetValueOrDefault("MercadoLivre__ApiBaseUrl")?.TrimEnd('/'));
        Assert.Equal("true", api.GetValueOrDefault("MercadoLivre__AllowNonOfficialEndpoints"), ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(api.GetValueOrDefault("MercadoLivre__ClientId")), "MercadoLivre__ClientId vazio no ambiente local.");
        Assert.False(string.IsNullOrWhiteSpace(api.GetValueOrDefault("MercadoLivre__ClientSecret")), "MercadoLivre__ClientSecret vazio no ambiente local.");
        Assert.True(long.TryParse(api["MercadoLivre__ClientId"], out _), "MercadoLivre__ClientId precisa ser numérico (é o application_id das notificações).");

        // E o simulador usa as mesmas credenciais sintéticas, aceita o RedirectUri da Api e notifica a Api pela rede interna
        var sim = Environment(simulator);
        Assert.Equal(api["MercadoLivre__ClientId"], sim.GetValueOrDefault("Simulator__ClientId"));
        Assert.Equal(api["MercadoLivre__ClientSecret"], sim.GetValueOrDefault("Simulator__ClientSecret"));
        var redirects = sim.Where(kv => kv.Key.StartsWith("Simulator__AllowedRedirectUris__", StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
        Assert.Contains(api.GetValueOrDefault("MercadoLivre__RedirectUri"), redirects);
        Assert.Equal("http://api:8080/api/v1/mercadolivre/notifications", sim.GetValueOrDefault("Simulator__NotificationUrl"));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-03")]
    public void So_o_env_example_local_liga_o_perfil_e_define_o_diretorio_de_sites_extras()
    {
        // Dado os arquivos de deploy e de pipeline
        var localExample = RepoRoot.Combine("deploy", "local", ".env.example");
        var candidates = ScannedDirectories
            .Select(d => RepoRoot.Combine(d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.state{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !string.Equals(Path.GetFileName(f), ".env", StringComparison.Ordinal))
            .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(localExample), StringComparison.Ordinal))
            .ToList();

        // Então nenhum outro arquivo liga o perfil ml-simulator nem define CADDY_EXTRA_SITES_DIR
        var offenders = new List<string>();
        foreach (var file in candidates)
        {
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if ((ProfilesLine().IsMatch(line) && line.Contains("ml-simulator", StringComparison.Ordinal)) || ExtraSitesDefinition().IsMatch(line))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot.Path, file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"Só deploy/local/.env.example pode ligar o simulador:\n{string.Join('\n', offenders)}");

        // E o deploy/local/.env.example liga os dois
        var example = File.ReadAllLines(localExample);
        Assert.Contains(example, l => ProfilesLine().IsMatch(l) && l.Contains("ml-simulator", StringComparison.Ordinal) && l.Contains("db-container", StringComparison.Ordinal));
        Assert.Contains(example, l => ExtraSitesDefinition().IsMatch(l) && l.TrimEnd().EndsWith("local/caddy-sites", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-03")]
    public void Caddyfile_de_producao_nao_cita_o_simulador()
    {
        var caddyfile = File.ReadAllText(RepoRoot.Combine("deploy", "Caddyfile"));

        Assert.DoesNotMatch(SimulatorMention(), caddyfile);
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-03")]
    public async Task Diretorio_padrao_de_sites_extras_esta_vazio_e_o_local_serve_o_simulador()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o Caddy da configuração padrão: o único bind além do Caddyfile é o diretório de sites extras
        var standard = ExtraSitesDirectory(await ComposeConfigAsync(EnvMode.Default, profiles: null, cancellationToken));

        // Então ele existe no repositório e está vazio (só arquivos ocultos, como .gitkeep)
        Assert.True(Directory.Exists(standard), $"Diretório padrão de sites extras inexistente: {standard}");
        var visible = Directory.EnumerateFileSystemEntries(standard).Where(e => !Path.GetFileName(e).StartsWith('.')).ToList();
        Assert.True(visible.Count == 0, $"Diretório padrão de sites extras não está vazio: [{string.Join(", ", visible.Select(Path.GetFileName))}]");

        // E o do ambiente local serve ml-simulador.vitrine.localhost -> ml-simulator:8080
        var local = ExtraSitesDirectory(await ComposeConfigAsync(EnvMode.Local, profiles: null, cancellationToken));
        Assert.NotEqual(Path.GetFullPath(standard), Path.GetFullPath(local));
        var sites = string.Join('\n', Directory.EnumerateFiles(local).Where(f => !Path.GetFileName(f).StartsWith('.')).Select(File.ReadAllText));
        Assert.Contains(SimulatorHost, sites, StringComparison.Ordinal);
        Assert.Contains("ml-simulator:8080", sites, StringComparison.Ordinal);
    }

    private enum EnvMode
    {
        /// <summary>deploy/local/.env.example sem COMPOSE_PROFILES e sem CADDY_EXTRA_SITES_DIR (como staging/produção).</summary>
        Default,

        /// <summary>deploy/local/.env.example como está.</summary>
        Local,
    }

    private static async Task<JsonElement> ComposeConfigAsync(EnvMode mode, string? profiles, CancellationToken cancellationToken, bool overrideFile = true)
    {
        using var temp = new TempDirectory();
        var lines = File.ReadAllLines(RepoRoot.Combine("deploy", "local", ".env.example"))
            .Where(l => mode == EnvMode.Local || !(ProfilesLine().IsMatch(l) || ExtraSitesDefinition().IsMatch(l)))
            .Append("IMAGE_TAG=spec0024-it03");
        var envFile = Path.Combine(temp.Path, "env");
        await File.WriteAllLinesAsync(envFile, lines, cancellationToken);

        var arguments = new List<string>
        {
            "compose", "--project-directory", RepoRoot.Combine("deploy"), "--env-file", envFile,
            "-f", RepoRoot.Combine("deploy", "compose.yaml"),
        };
        if (overrideFile)
        {
            arguments.AddRange(["-f", RepoRoot.Combine("deploy", "compose.override.yaml")]);
        }

        arguments.AddRange(["config", "--format", "json"]);
        var environment = new Dictionary<string, string?> { ["CADDY_EXTRA_SITES_DIR"] = null, ["COMPOSE_PROFILES"] = profiles };

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync("docker", arguments, RepoRoot.Path, ComposeTimeout, environment, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("docker não encontrado: SPEC-0024:IT-03 lê o Compose com `docker compose config`.", ex);
        }

        Assert.True(result.ExitCode == 0, $"docker compose config ({mode}, perfis '{profiles}') falhou:\n{result.Output}");
        return JsonDocument.Parse(result.StdOut).RootElement.Clone();
    }

    private static Dictionary<string, string> Environment(JsonElement service) =>
        service.TryGetProperty("environment", out var env)
            ? env.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? string.Empty : p.Value.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Origem do bind do Caddy que não é o Caddyfile (o diretório de sites extras); exatamente um.</summary>
    private static string ExtraSitesDirectory(JsonElement config)
    {
        var binds = config.GetProperty("services").GetProperty("caddy").GetProperty("volumes").EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "bind" && v.GetProperty("target").GetString() != "/etc/caddy/Caddyfile")
            .Select(v => v.GetProperty("source").GetString()!)
            .ToList();
        Assert.True(binds.Count == 1, $"O Caddy deveria montar exatamente um diretório de sites extras; binds: [{string.Join(", ", binds)}]");
        return binds[0];
    }

    [GeneratedRegex(@"^\s*(export\s+)?""?COMPOSE_PROFILES""?\s*[=:]")]
    private static partial Regex ProfilesLine();

    [GeneratedRegex(@"^\s*(-\s*)?(export\s+)?""?CADDY_EXTRA_SITES_DIR""?\s*[=:]")]
    private static partial Regex ExtraSitesDefinition();

    [GeneratedRegex("ml-simulat|ml-simulad|simulator|simulador", RegexOptions.IgnoreCase)]
    private static partial Regex SimulatorMention();
}
