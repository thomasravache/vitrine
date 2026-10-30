using System.Xml.Linq;

namespace Vitrine.ArchitectureTests;

/// <summary>
/// SPEC-0024 (ADR-0016, Confirmação): o simulador do Mercado Livre é ferramenta de dev/E2E; a imagem da Api nunca o
/// carrega. Verificado no grafo de <c>ProjectReference</c> a partir do host.
/// </summary>
public sealed class MercadoLivreSimulatorIsolationTests
{
    private const string SimulatorProject = "Vitrine.MercadoLivreSimulator.csproj";
    private const string SimulatorRelativePath = "tools/MercadoLivreSimulator/Vitrine.MercadoLivreSimulator.csproj";

    [Fact]
    [Trait("spec", "SPEC-0024:UT-04")]
    public void Api_e_o_que_ela_referencia_nao_referenciam_o_simulador()
    {
        // Dado o grafo de projetos da solução
        var root = RepositoryRoot();
        var solution = XDocument.Load(Path.Combine(root, "Vitrine.slnx"));
        var solutionProjects = solution.Descendants("Project")
            .Select(p => ((string?)p.Attribute("Path") ?? string.Empty).Replace('\\', '/'))
            .ToList();
        Assert.True(
            solutionProjects.Contains(SimulatorRelativePath, StringComparer.Ordinal),
            $"O simulador ({SimulatorRelativePath}) não está no Vitrine.slnx. Projetos: [{string.Join(", ", solutionProjects)}]");

        // Quando o fecho transitivo de ProjectReference da Api é percorrido
        var api = Path.GetFullPath(Path.Combine(root, "src", "Api", "Vitrine.Api.csproj"));
        var closure = TransitiveReferences(api);

        // Então nenhum projeto do fecho (nem a própria Api) é o simulador
        var offenders = closure
            .Where(p => string.Equals(Path.GetFileName(p), SimulatorProject, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(
            offenders.Count == 0,
            $"Vitrine.Api alcança o simulador por ProjectReference: [{string.Join(", ", closure.Select(p => Path.GetRelativePath(root, p)))}]");

        // E nenhum projeto do fecho fica em tools/
        var tools = Path.Combine(root, "tools") + Path.DirectorySeparatorChar;
        var fromTools = closure.Where(p => p.StartsWith(tools, StringComparison.Ordinal)).ToList();
        Assert.True(
            fromTools.Count == 0,
            $"Vitrine.Api referencia projetos de tools/: [{string.Join(", ", fromTools.Select(p => Path.GetRelativePath(root, p)))}]");
    }

    private static HashSet<string> TransitiveReferences(string project)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([project]);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            Assert.True(File.Exists(current), $"ProjectReference para projeto inexistente: {current}");
            var directory = Path.GetDirectoryName(current)!;
            foreach (var reference in XDocument.Load(current).Descendants("ProjectReference"))
            {
                var include = (string?)reference.Attribute("Include");
                if (!string.IsNullOrWhiteSpace(include))
                {
                    pending.Push(Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
                }
            }
        }

        return visited;
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
