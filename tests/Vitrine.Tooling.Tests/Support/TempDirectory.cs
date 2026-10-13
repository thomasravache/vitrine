namespace Vitrine.Tooling.Tests.Support;

/// <summary>Diretório temporário exclusivo do teste, removido no descarte.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("vitrine-tooling-").FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Limpeza best-effort: um arquivo preso não deve mascarar o resultado do teste.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }
}
