namespace Vitrine.IntegrationTests.Support;

/// <summary>
/// Web root temporário com um <c>index.html</c> de teste (a SPA publicada em <c>wwwroot</c> — ADR-0013), para o host
/// subir com o fallback da SPA. O corpo traz <see cref="Marker"/>, que identifica o <c>index.html</c> numa resposta.
/// </summary>
public sealed class TestWebRoot : IDisposable
{
    public const string Marker = "vitrine-spa-de-teste";

    public TestWebRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vitrine-webroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        File.WriteAllText(
            System.IO.Path.Combine(Path, "index.html"),
            $"<!doctype html><html><head><title>Vitrine</title></head><body><div id=\"root\"></div><!-- {Marker} --></body></html>");
    }

    /// <summary>Caminho absoluto do web root.</summary>
    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária: sobra no máximo um index.html de teste.
        }
    }
}
