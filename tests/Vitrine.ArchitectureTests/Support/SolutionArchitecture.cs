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
    /// Todo projeto .csproj em <c>src/</c> entra nas regras, referenciado ou não pelo host: o assembly vem da pasta
    /// de saída dos testes ou, se não estiver lá, da saída do próprio projeto (mesma configuração de build).
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
    /// em <paramref name="outputDirectory"/> (saída do projeto de testes).
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
            .Select(project => LoadProjectAssembly(project, output, configuration, targetFramework))
            .ToList();
    }

    private static Assembly LoadProjectAssembly(
        string project,
        string outputDirectory,
        string configuration,
        string targetFramework)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var candidates = new[]
        {
            Path.Combine(outputDirectory, name + ".dll"),
            Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, targetFramework, name + ".dll"),
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"Assembly de '{project}' não encontrado ({string.Join(", ", candidates)}). Compile a solução antes.");

        return Path.GetDirectoryName(path) == outputDirectory
            ? Assembly.Load(AssemblyName.GetAssemblyName(path))
            : Assembly.LoadFile(path);
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
