using System.Text.Json;
using System.Text.RegularExpressions;
using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

/// <summary>
/// SPEC-0039:IT-01 — o Caddy do ambiente local prod-like só escuta em <c>127.0.0.1</c> (o Docker publica portas por
/// regras próprias do iptables, que passam antes das do ufw); em staging/produção continua em todas as interfaces.
/// O Compose é lido por <c>docker compose config</c> (os mesmos arquivos do <c>deploy/deploy.sh</c>).
/// </summary>
public sealed partial class CaddyBindAddressTests
{
    private static readonly TimeSpan ComposeTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] ExpectedPorts = ["443/tcp", "443/udp", "80/tcp"];

    [Fact]
    [Trait("spec", "SPEC-0039:IT-01")]
    public async Task Ambiente_local_publica_o_Caddy_so_em_127_0_0_1()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o Compose resolvido com o deploy/local/.env.example como está
        var ports = CaddyPorts(await ComposeConfigAsync(local: true, cancellationToken));

        // Então as três portas publicadas do caddy escutam só em 127.0.0.1
        Assert.Equal(ExpectedPorts, ports.Select(p => p.Port).Order(StringComparer.Ordinal).ToArray());
        Assert.All(ports, p => Assert.True(
            p.HostIp == "127.0.0.1",
            $"Porta {p.Port} do caddy publicada em '{p.HostIp ?? "(sem host_ip: todas as interfaces)"}' no ambiente local; esperado 127.0.0.1."));
    }

    [Fact]
    [Trait("spec", "SPEC-0039:IT-01")]
    public async Task Staging_e_producao_publicam_o_Caddy_em_todas_as_interfaces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o Compose resolvido sem CADDY_BIND_ADDRESS (configuração de staging/produção)
        var ports = CaddyPorts(await ComposeConfigAsync(local: false, cancellationToken));

        // Então as três portas continuam em todas as interfaces (0.0.0.0, ou sem host_ip)
        Assert.Equal(ExpectedPorts, ports.Select(p => p.Port).Order(StringComparer.Ordinal).ToArray());
        Assert.All(ports, p => Assert.True(
            p.HostIp is null or "" or "0.0.0.0",
            $"Porta {p.Port} do caddy publicada em '{p.HostIp}' sem CADDY_BIND_ADDRESS; esperado 0.0.0.0."));
    }

    private sealed record PublishedPort(string Port, string? HostIp);

    private static List<PublishedPort> CaddyPorts(JsonElement config) =>
        config.GetProperty("services").GetProperty("caddy").GetProperty("ports").EnumerateArray()
            .Select(p => new PublishedPort(
                $"{p.GetProperty("published").ToString()}/{(p.TryGetProperty("protocol", out var proto) ? proto.GetString() : "tcp")}",
                p.TryGetProperty("host_ip", out var ip) ? ip.GetString() : null))
            .ToList();

    /// <param name="local">
    /// <c>true</c>: deploy/local/.env.example como está. <c>false</c>: o mesmo arquivo sem CADDY_BIND_ADDRESS (como
    /// staging/produção). Nos dois casos a variável não vem do ambiente do processo.
    /// </param>
    private static async Task<JsonElement> ComposeConfigAsync(bool local, CancellationToken cancellationToken)
    {
        using var temp = new TempDirectory();
        var lines = File.ReadAllLines(RepoRoot.Combine("deploy", "local", ".env.example"))
            .Where(l => local || !BindAddressDefinition().IsMatch(l))
            .Append("IMAGE_TAG=spec0039-it01");
        var envFile = Path.Combine(temp.Path, "env");
        await File.WriteAllLinesAsync(envFile, lines, cancellationToken);

        string[] arguments =
        [
            "compose", "--project-directory", RepoRoot.Combine("deploy"), "--env-file", envFile,
            "-f", RepoRoot.Combine("deploy", "compose.yaml"),
            "-f", RepoRoot.Combine("deploy", "compose.override.yaml"),
            "config", "--format", "json",
        ];
        var environment = new Dictionary<string, string?> { ["CADDY_BIND_ADDRESS"] = null };

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync("docker", arguments, RepoRoot.Path, ComposeTimeout, environment, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("docker não encontrado: SPEC-0039:IT-01 lê o Compose com `docker compose config`.", ex);
        }

        Assert.True(result.ExitCode == 0, $"docker compose config (local: {local}) falhou:\n{result.Output}");
        return JsonDocument.Parse(result.StdOut).RootElement.Clone();
    }

    [GeneratedRegex(@"^\s*(export\s+)?""?CADDY_BIND_ADDRESS""?\s*=")]
    private static partial Regex BindAddressDefinition();
}
