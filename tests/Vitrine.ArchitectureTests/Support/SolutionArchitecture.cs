using System.Reflection;
using ArchUnitNET.Loader;
using Architecture = ArchUnitNET.Domain.Architecture;

namespace Vitrine.ArchitectureTests.Support;

/// <summary>
/// Arquitetura dos assemblies reais da solução (host, AppHost, ServiceDefaults, Common e módulos em src/),
/// sem os assemblies de amostra de <c>Samples/</c>.
/// </summary>
public static class SolutionArchitecture
{
    private static readonly string[] ExcludedSegments = ["bin", "obj", "node_modules"];

    private static readonly Lazy<Architecture> Architecture = new(() => new ArchLoader()
        .LoadAssemblies([.. SolutionAssemblies()])
        .Build());

    /// <summary>
    /// Todo projeto .csproj em <c>src/</c> entra nas regras. O assembly vem só da pasta de saída dos testes (como no
    /// job <c>architecture</c> do CI, que compila só este projeto): projeto que não chega lá por referência falha o
    /// carregamento, em vez de passar localmente por um <c>src/**/bin</c> antigo (SPEC-0027).
    /// </summary>
    public static Architecture Load() => Architecture.Value;

    private static List<Assembly> SolutionAssemblies()
    {
        var configuration = typeof(SolutionArchitecture).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var outputDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var targetFramework = new DirectoryInfo(outputDirectory).Name;

        return [.. LoadProjectAssemblies(RepositoryRoot(), outputDirectory, configuration, targetFramework)];
    }

    /// <summary>
    /// Carrega o assembly de cada projeto .csproj em <c>src/</c> de <paramref name="repositoryRoot"/>, procurando-o
    /// só em <paramref name="outputDirectory"/> (saída do projeto de testes). <paramref name="configuration"/> e
    /// <paramref name="targetFramework"/> ficam no contrato do carregador, mas não abrem mais o fallback para
    /// <c>src/**/bin</c>: assembly ausente da saída é projeto não referenciado (<see cref="InvalidOperationException"/>).
    /// </summary>
    public static IReadOnlyList<Assembly> LoadProjectAssemblies(
        string repositoryRoot,
        string outputDirectory,
        string configuration,
        string targetFramework)
    {
        var src = Path.Combine(repositoryRoot, "src");
        var output = outputDirectory.TrimEnd(Path.DirectorySeparatorChar);

        return Directory
            .EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !Path.GetRelativePath(src, project)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .Select(project => LoadProjectAssembly(project, output))
            .ToList();
    }

    private static Assembly LoadProjectAssembly(string project, string outputDirectory)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var path = Path.Combine(outputDirectory, name + ".dll");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"O projeto '{name}' ({project}) não é referenciado pelos projetos que chegam ao Vitrine.ArchitectureTests: "
                + $"'{path}' não existe. Referencie-o a partir da solução — a camada Application do módulo deve referenciar "
                + "seu IntegrationEvents/PublicApi — em vez de depender de um build da solução inteira.");
        }

        try
        {
            // Referenciado: resolvido pelo deps.json do projeto de testes.
            return Assembly.Load(AssemblyName.GetAssemblyName(path));
        }
        catch (FileNotFoundException)
        {
            // Copiado para a saída sem entrar no deps.json (o AppHost, executável): carregado pelo caminho, na mesma pasta.
            return Assembly.LoadFile(path);
        }
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
