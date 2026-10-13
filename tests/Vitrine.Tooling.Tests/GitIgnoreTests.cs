using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

public sealed class GitIgnoreTests
{
    [Theory]
    [Trait("spec", "SPEC-0002:IT-03")]
    [InlineData(".env")]
    [InlineData(".env.production")]
    [InlineData("src/Api/appsettings.Production.local.json")]
    [InlineData("aspire-output/x")]
    public async Task Arquivo_de_segredo_ou_saida_local_e_ignorado_pelo_git(string path)
    {
        // Dado o .gitignore do repositório (sem excludes globais da máquina)
        // Quando git check-ignore é executado para o caminho
        var result = await ProcessRunner.RunAsync(
            "git",
            ["-c", "core.excludesFile=/dev/null", "check-ignore", "--no-index", "--verbose", path],
            RepoRoot.Path,
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string?>
            {
                ["GIT_CONFIG_GLOBAL"] = "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            },
            TestContext.Current.CancellationToken);

        // Então o caminho é ignorado (exit 0); exit 1 = não ignorado; outro = erro do git
        Assert.True(
            result.ExitCode == 0,
            $"'{path}' não é ignorado pelo git (check-ignore exit {result.ExitCode}).{Environment.NewLine}{result.Output}");
    }
}
