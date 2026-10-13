using Vitrine.Tooling.Tests.Support;

namespace Vitrine.Tooling.Tests;

public sealed class GitHooksTests
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private static string HooksPath => RepoRoot.Combine(".githooks");

    [Theory]
    [Trait("spec", "SPEC-0002:IT-02")]
    [InlineData("atualiza coisas")]
    [InlineData("feat(repo): estrutura inicial")]
    public async Task Hook_commit_msg_rejeita_mensagem_fora_do_padrao_ou_sem_refs(string message)
    {
        AssertCommitMsgHookExists();
        using var repo = new TempDirectory();
        await InitRepositoryAsync(repo.Path);

        // Quando se tenta commitar uma mensagem fora de Conventional Commits ou sem "Refs: SPEC-NNNN"
        var result = await CommitAsync(repo.Path, message);

        // Então o commit é rejeitado
        Assert.True(
            result.ExitCode != 0,
            $"Commit com mensagem '{message}' deveria ser rejeitado pelo hook commit-msg.{Environment.NewLine}{result.Output}");
        var log = await GitAsync(repo.Path, "rev-list", "--all", "--count");
        Assert.Equal("0", log.StdOut.Trim());
    }

    [Fact]
    [Trait("spec", "SPEC-0002:IT-02")]
    public async Task Hook_commit_msg_aceita_conventional_commit_com_refs()
    {
        AssertCommitMsgHookExists();
        using var repo = new TempDirectory();
        await InitRepositoryAsync(repo.Path);

        // Quando se commita "feat(repo): estrutura inicial" com "Refs: SPEC-0002"
        var result = await CommitAsync(repo.Path, "feat(repo): estrutura inicial\n\nRefs: SPEC-0002\n");

        // Então o commit é aceito
        Assert.True(result.ExitCode == 0, $"Commit no padrão deveria ser aceito.{Environment.NewLine}{result.Output}");
        var log = await GitAsync(repo.Path, "log", "-1", "--format=%s");
        Assert.Equal("feat(repo): estrutura inicial", log.StdOut.Trim());
    }

    private static void AssertCommitMsgHookExists()
    {
        var hook = Path.Combine(HooksPath, "commit-msg");
        Assert.True(File.Exists(hook), $"Hook não encontrado: {hook}");
    }

    // Dado um repositório git temporário com core.hooksPath apontando para .githooks do repositório
    private static async Task InitRepositoryAsync(string path)
    {
        await GitOkAsync(path, "init", "--quiet", "--initial-branch=main");
        await GitOkAsync(path, "config", "user.name", "Vitrine Tooling Tests");
        await GitOkAsync(path, "config", "user.email", "tooling-tests@vitrine.invalid");
        await GitOkAsync(path, "config", "commit.gpgsign", "false");
        await GitOkAsync(path, "config", "core.hooksPath", HooksPath);
        await File.WriteAllTextAsync(Path.Combine(path, "arquivo.txt"), "conteúdo\n", TestContext.Current.CancellationToken);
        await GitOkAsync(path, "add", "arquivo.txt");
    }

    private static async Task<ProcessResult> CommitAsync(string path, string message)
    {
        var messageFile = Path.Combine(path, ".git", "TEST_COMMIT_MSG");
        await File.WriteAllTextAsync(messageFile, message, TestContext.Current.CancellationToken);
        return await GitAsync(path, "commit", "--quiet", "--file", messageFile);
    }

    private static async Task GitOkAsync(string path, params string[] args)
    {
        var result = await GitAsync(path, args);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} falhou:{Environment.NewLine}{result.Output}");
    }

    private static Task<ProcessResult> GitAsync(string path, params string[] args) =>
        ProcessRunner.RunAsync(
            "git",
            args,
            path,
            GitTimeout,
            // Isola da configuração global/sistema da máquina (hooks, templates, assinatura).
            new Dictionary<string, string?>
            {
                ["GIT_CONFIG_GLOBAL"] = "/dev/null",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_TEMPLATE_DIR"] = string.Empty,
            },
            TestContext.Current.CancellationToken);
}
