using System.Globalization;
using System.Text.Json;
using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

public sealed class VulnerableDependencyTests
{
    private enum Ecosystem
    {
        NuGet,
        Npm,
    }

    /// <summary>
    /// Faixa vulnerável de um aviso: versões abaixo de <paramref name="Limit"/>
    /// (ou até ela, inclusive, quando <paramref name="Inclusive"/>) são vulneráveis.
    /// </summary>
    private sealed record Rule(string Package, Ecosystem Ecosystem, string Limit, bool Inclusive, string Advisory)
    {
        public bool IsVulnerable(string version)
        {
            var comparison = CompareVersions(version, Limit);
            return Inclusive ? comparison <= 0 : comparison < 0;
        }

        public string Range => (Inclusive ? "<= " : "< ") + Limit;
    }

    private static readonly Rule[] Rules =
    [
        new("MessagePack", Ecosystem.NuGet, "2.5.303", Inclusive: false, "GHSA-qhrr-8q5h-9q3h"),
        new("undici", Ecosystem.Npm, "7.30.1", Inclusive: false, "GHSA-4w7w-5xrc-9h26, GHSA-2m5v-pch6-74hx, GHSA-qrxm-fqhp-jwvm, GHSA-xjj6-f33q-929c e outros"),
        new("happy-dom", Ecosystem.Npm, "21.0.3", Inclusive: true, "GHSA-834v-8h66-3h79"),
    ];

    private static readonly string[] NuGetLockFiles =
    [
        "src/AppHost/packages.lock.json",
        "tests/Vitrine.Tooling.Tests/packages.lock.json",
        "web/e2e/support/packages.lock.json",
    ];

    private const string NpmLockFile = "web/package-lock.json";

    [Fact]
    [Trait("spec", "SPEC-0010:UT-01")]
    public async Task Lockfiles_nao_resolvem_versoes_com_avisos_de_seguranca_conhecidos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado os lockfiles versionados (NuGet e npm)
        var resolved = new List<(string File, Ecosystem Ecosystem, string Package, string Version)>();
        foreach (var lockFile in NuGetLockFiles)
        {
            resolved.AddRange(await ReadNuGetLockAsync(lockFile, cancellationToken));
        }

        resolved.AddRange(await ReadNpmLockAsync(NpmLockFile, cancellationToken));

        // Quando as versões resolvidas são comparadas às faixas vulneráveis dos avisos
        var violations = resolved
            .SelectMany(r => Rules
                .Where(rule => rule.Ecosystem == r.Ecosystem
                    && string.Equals(rule.Package, r.Package, StringComparison.OrdinalIgnoreCase)
                    && rule.IsVulnerable(r.Version))
                .Select(rule => $"  {r.File}: {r.Package} {r.Version} (vulnerável {rule.Range}; {rule.Advisory})"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Então nenhuma versão resolvida está numa faixa vulnerável
        Assert.True(
            violations.Count == 0,
            "Dependências resolvidas em versões com avisos de segurança conhecidos:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Formato NuGet: dependencies → framework → pacote → "resolved".</summary>
    private static async Task<IEnumerable<(string, Ecosystem, string, string)>> ReadNuGetLockAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        using var json = await ParseAsync(relativePath, cancellationToken);
        var result = new List<(string, Ecosystem, string, string)>();
        foreach (var framework in json.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var package in framework.Value.EnumerateObject())
            {
                if (package.Value.TryGetProperty("resolved", out var version) && version.GetString() is { } v)
                {
                    result.Add((relativePath, Ecosystem.NuGet, package.Name, v));
                }
            }
        }

        Assert.NotEmpty(result);
        return result;
    }

    /// <summary>Formato npm v3: "packages" → "node_modules/.../&lt;nome&gt;" → "version" (inclui aninhados).</summary>
    private static async Task<IEnumerable<(string, Ecosystem, string, string)>> ReadNpmLockAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        const string marker = "node_modules/";
        using var json = await ParseAsync(relativePath, cancellationToken);
        var result = new List<(string, Ecosystem, string, string)>();
        foreach (var entry in json.RootElement.GetProperty("packages").EnumerateObject())
        {
            var index = entry.Name.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0
                || entry.Value.TryGetProperty("link", out _)
                || !entry.Value.TryGetProperty("version", out var version)
                || version.GetString() is not { } v)
            {
                continue;
            }

            result.Add((relativePath, Ecosystem.Npm, entry.Name[(index + marker.Length)..], v));
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static async Task<JsonDocument> ParseAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = RepoRoot.Combine(relativePath.Split('/'));
        Assert.True(File.Exists(path), $"Lockfile ausente: {relativePath}");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Compara versões SemVer/NuGet: partes numéricas primeiro; uma pré-release
    /// (sufixo após '-') é menor que a versão final correspondente. Metadados ('+') são ignorados.
    /// </summary>
    private static int CompareVersions(string left, string right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);

        for (var i = 0; i < Math.Max(leftCore.Length, rightCore.Length); i++)
        {
            var comparison = (i < leftCore.Length ? leftCore[i] : 0).CompareTo(i < rightCore.Length ? rightCore[i] : 0);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return (leftPre, rightPre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.Compare(leftPre, rightPre, StringComparison.OrdinalIgnoreCase),
        };

        static (long[] Core, string? PreRelease) Split(string version)
        {
            var withoutMetadata = version.Split('+', 2)[0];
            var parts = withoutMetadata.Split('-', 2);
            var core = parts[0]
                .Split('.')
                .Select(p => long.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture))
                .ToArray();
            return (core, parts.Length > 1 ? parts[1] : null);
        }
    }
}
