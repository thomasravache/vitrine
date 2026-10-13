using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using Assembly = System.Reflection.Assembly;

namespace Vitrine.ArchitectureTests.Support;

/// <summary>
/// Arquitetura dos assemblies reais da solução (host, ServiceDefaults, Common e módulos em src/),
/// sem os assemblies de amostra de <c>Samples/</c>.
/// </summary>
public static class SolutionArchitecture
{
    private const string SolutionPrefix = "Vitrine.";

    private static readonly Lazy<Architecture> Architecture = new(() => new ArchLoader()
        .LoadAssemblies([.. SolutionAssemblies(typeof(Program).Assembly)])
        .Build());

    /// <summary>
    /// O host <c>Vitrine.Api</c> compõe a solução (ADR-0001): parte dele e segue as referências
    /// <c>Vitrine.*</c>, de modo que todo módulo ligado ao host entra nas regras sem cadastro manual.
    /// </summary>
    public static Architecture Load() => Architecture.Value;

    private static List<Assembly> SolutionAssemblies(Assembly host)
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal) { [host.GetName().Name!] = host };
        var pending = new Queue<Assembly>([host]);
        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is { } name
                    && name.StartsWith(SolutionPrefix, StringComparison.Ordinal)
                    && !found.ContainsKey(name))
                {
                    var loaded = Assembly.Load(reference);
                    found[name] = loaded;
                    pending.Enqueue(loaded);
                }
            }
        }

        return [.. found.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal)];
    }
}
