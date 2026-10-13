namespace Vitrine.Tooling.Tests.Support;

/// <summary>Localiza a raiz do repositório subindo diretórios até encontrar <c>Vitrine.slnx</c>.</summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> Root = new(Find);

    public static string Path => Root.Value;

    public static string Combine(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Vitrine.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Vitrine.slnx não encontrado subindo a partir de '{AppContext.BaseDirectory}'.");
    }
}
