using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

public sealed class LintTests
{
    private static readonly TimeSpan LintTimeout = TimeSpan.FromMinutes(10);

    // Não copiados: histórico git, saídas de build e artefatos locais.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        ".git", "bin", "obj", "aspire-output", "TestResults", ".vs", ".idea",
    };

    public static TheoryData<string, string, string> BadlyFormattedFiles => new()
    {
        {
            "C#",
            "src/Api/LintProbe.cs",
            "namespace Vitrine.Api;\npublic   class LintProbe{public int  Value( ){return 1;}}\n"
        },
        {
            "TypeScript",
            "web/src/lint-probe.ts",
            "export const lintProbe   =   {a:1,\"b\" : 2}\n"
        },
    };

    [Theory]
    [Trait("spec", "SPEC-0002:IT-04")]
    [MemberData(nameof(BadlyFormattedFiles))]
    public async Task Comando_lint_falha_com_arquivo_mal_formatado(string language, string relativePath, string content)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var lint = SddConfig.ReadCommand("lint");
        Assert.False(
            string.IsNullOrWhiteSpace(lint),
            $"Comando 'lint' vazio em {SddConfig.ConfigPath}: configure-o (contrato da SPEC-0002).");

        // Dado uma cópia do repositório com um arquivo mal formatado
        using var copy = new TempDirectory();
        CopyRepository(RepoRoot.Path, copy.Path);
        await InitGitAsync(copy.Path, cancellationToken);

        var target = Path.Combine(copy.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var targetDir = Path.GetDirectoryName(target)!;
        Assert.True(Directory.Exists(targetDir), $"Pasta '{Path.GetDirectoryName(relativePath)}' não existe no repositório ({language}).");
        await File.WriteAllTextAsync(target, content, cancellationToken);

        // Quando o comando lint do sdd-config roda na raiz
        var result = await ProcessRunner.RunAsync(
            "bash",
            ["-c", lint],
            copy.Path,
            LintTimeout,
            cancellationToken: cancellationToken);

        // Então termina com código diferente de zero, apontando o arquivo mal formatado
        Assert.True(
            result.ExitCode != 0,
            $"'lint' deveria falhar com arquivo {language} mal formatado ({relativePath}).{Environment.NewLine}{result.Output}");
        Assert.Contains(Path.GetFileName(relativePath), result.Output, StringComparison.Ordinal);
    }

    private static async Task InitGitAsync(string path, CancellationToken cancellationToken)
    {
        // Algumas ferramentas (ex.: Biome com vcs.useIgnoreFile) esperam a raiz de um repositório git.
        var result = await ProcessRunner.RunAsync(
            "git",
            ["init", "--quiet"],
            path,
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = "/dev/null", ["GIT_CONFIG_NOSYSTEM"] = "1" },
            cancellationToken);
        Assert.True(result.ExitCode == 0, $"git init falhou:{Environment.NewLine}{result.Output}");
    }

    private static void CopyRepository(string source, string destination)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            switch (entry)
            {
                // Em worktrees, ".git" é um arquivo que aponta para o repositório real: nunca copiar.
                case FileSystemInfo when entry.Name == ".git":
                    break;
                case DirectoryInfo dir when SkippedDirectories.Contains(dir.Name):
                    break;
                case DirectoryInfo dir when dir.Name == "node_modules":
                    // Dependências instaladas são reaproveitadas, não copiadas.
                    Directory.CreateSymbolicLink(target, dir.FullName);
                    break;
                case DirectoryInfo dir when dir.LinkTarget is not null:
                    break;
                case DirectoryInfo dir:
                    Directory.CreateDirectory(target);
                    CopyRepository(dir.FullName, target);
                    break;
                case FileInfo file:
                    file.CopyTo(target);
                    break;
            }
        }
    }
}
