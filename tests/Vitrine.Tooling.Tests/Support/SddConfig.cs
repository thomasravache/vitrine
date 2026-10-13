using System.Text.RegularExpressions;

namespace Vitrine.Tooling.Tests.Support;

/// <summary>Lê comandos de nível superior de <c>docs/specs/sdd-config.yml</c> (chave: "valor").</summary>
internal static partial class SddConfig
{
    public static string ConfigPath => RepoRoot.Combine("docs", "specs", "sdd-config.yml");

    public static string ReadCommand(string key)
    {
        foreach (var line in File.ReadLines(ConfigPath))
        {
            var match = TopLevelEntry().Match(line);
            if (!match.Success || match.Groups["key"].Value != key)
            {
                continue;
            }

            if (match.Groups["dq"].Success)
            {
                return match.Groups["dq"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            if (match.Groups["sq"].Success)
            {
                return match.Groups["sq"].Value.Replace("''", "'");
            }

            return match.Groups["plain"].Value.Trim();
        }

        return string.Empty;
    }

    [GeneratedRegex("""^(?<key>[A-Za-z_][A-Za-z0-9_]*):\s*(?:"(?<dq>(?:[^"\\]|\\.)*)"|'(?<sq>(?:[^']|'')*)'|(?<plain>[^#]*?))\s*(?:#.*)?$""")]
    private static partial Regex TopLevelEntry();
}
