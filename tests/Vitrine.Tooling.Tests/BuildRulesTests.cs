using System.Text.Json;
using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

public sealed class BuildRulesTests
{
    private static readonly string[] ExcludedSegments = ["bin", "obj", "node_modules"];

    [Fact]
    [Trait("spec", "SPEC-0002:UT-02")]
    public async Task Todo_projeto_em_src_tem_nullable_warnings_como_erro_e_versoes_centralizadas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado qualquer .csproj em src/
        var srcDir = RepoRoot.Combine("src");
        var projects = Directory
            .EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(srcDir, p)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(projects);

        // Quando suas propriedades MSBuild avaliadas são lidas
        var violations = new List<string>();
        foreach (var project in projects)
        {
            var properties = await EvaluateAsync(project, cancellationToken);
            var relative = Path.GetRelativePath(RepoRoot.Path, project);

            // Então Nullable=enable, TreatWarningsAsErrors=true e ManagePackageVersionsCentrally=true
            Check(relative, properties, "Nullable", "enable", violations);
            Check(relative, properties, "TreatWarningsAsErrors", "true", violations);
            Check(relative, properties, "ManagePackageVersionsCentrally", "true", violations);
        }

        Assert.True(
            violations.Count == 0,
            "Projetos fora das regras de build:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static void Check(
        string project,
        IReadOnlyDictionary<string, string> properties,
        string name,
        string expected,
        List<string> violations)
    {
        var actual = properties.GetValueOrDefault(name, string.Empty);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add($"  {project}: {name}='{actual}' (esperado '{expected}')");
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> EvaluateAsync(
        string project,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "dotnet",
            [
                "msbuild",
                project,
                "-nologo",
                "-getProperty:Nullable",
                "-getProperty:TreatWarningsAsErrors",
                "-getProperty:ManagePackageVersionsCentrally",
            ],
            RepoRoot.Path,
            TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

        Assert.True(result.ExitCode == 0, $"Falha ao avaliar '{project}':{Environment.NewLine}{result.Output}");

        using var json = JsonDocument.Parse(result.StdOut);
        return json.RootElement
            .GetProperty("Properties")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }
}
