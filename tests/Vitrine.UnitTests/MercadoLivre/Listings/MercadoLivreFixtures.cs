using System.Text.Json;

namespace Vitrine.UnitTests.MercadoLivre.Listings;

/// <summary>Fixtures compartilhadas do ML em <c>tests/Fixtures/MercadoLivre</c>, lidas da raiz do repositório.</summary>
internal static class MercadoLivreFixtures
{
    public static JsonElement Load(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "Fixtures", "MercadoLivre", name);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Vitrine.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Vitrine.slnx não encontrado acima de '{AppContext.BaseDirectory}'.");
    }
}
