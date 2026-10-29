namespace Vitrine.IntegrationTests.Security.Support;

/// <summary>Cabeçalhos de segurança do contrato da SPEC-0008, comparados por diretiva (ordem e espaços não importam).</summary>
internal static class SecurityHeaders
{
    public const string HstsValue = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// Exige em <paramref name="response"/> todos os cabeçalhos do contrato; HSTS presente só quando
    /// <paramref name="expectHsts"/>; <c>connect-src</c> com a origem do Sentry só quando <paramref name="sentryOrigin"/>.
    /// </summary>
    public static void AssertContract(HttpResponseMessage response, bool expectHsts, string? sentryOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var where = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} ({(int)response.StatusCode})";

        var connectSrc = sentryOrigin is null ? new[] { "'self'" } : ["'self'", sentryOrigin];
        AssertCsp(
            Single(response, "Content-Security-Policy", where),
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["default-src"] = ["'self'"],
                ["script-src"] = ["'self'"],
                ["connect-src"] = connectSrc,
                ["frame-ancestors"] = ["'none'"],
            },
            where);

        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options", where), ignoreCase: true);
        Assert.Equal("strict-origin-when-cross-origin", Single(response, "Referrer-Policy", where), ignoreCase: true);
        Assert.Equal("DENY", Single(response, "X-Frame-Options", where), ignoreCase: true);
        AssertSameItems(
            ["camera=()", "microphone=()", "geolocation=()"],
            Split(Single(response, "Permissions-Policy", where), ','),
            $"Permissions-Policy em {where}");

        var hsts = Values(response, "Strict-Transport-Security");
        if (expectHsts)
        {
            Assert.True(hsts.Count == 1, $"Strict-Transport-Security ausente em {where}.");
            AssertSameItems(Split(HstsValue, ';'), Split(hsts[0], ';'), $"Strict-Transport-Security em {where}");
        }
        else
        {
            Assert.True(hsts.Count == 0, $"Strict-Transport-Security não deveria sair em {where}: {string.Join(" | ", hsts)}");
        }
    }

    /// <summary>Nenhum cabeçalho que revele o servidor.</summary>
    public static void AssertNoServerDisclosure(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var where = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}";
        Assert.True(Values(response, "Server").Count == 0, $"Cabeçalho Server presente em {where}: {string.Join(" | ", Values(response, "Server"))}");
        Assert.True(Values(response, "X-Powered-By").Count == 0, $"Cabeçalho X-Powered-By presente em {where}.");
    }

    public static List<string> Values(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        var values = new List<string>();
        if (response.Headers.TryGetValues(name, out var headerValues))
        {
            values.AddRange(headerValues);
        }

        if (response.Content is not null && response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            values.AddRange(contentValues);
        }

        return values;
    }

    private static string Single(HttpResponseMessage response, string name, string where)
    {
        var values = Values(response, name);
        Assert.True(values.Count == 1, $"{name} deveria sair uma vez em {where}; saiu {values.Count}: {string.Join(" | ", values)}");
        return values[0];
    }

    private static void AssertCsp(string csp, Dictionary<string, string[]> expected, string where)
    {
        var directives = csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.OrdinalIgnoreCase);

        AssertSameItems(expected.Keys, directives.Keys, $"diretivas da CSP em {where} (\"{csp}\")");
        foreach (var (name, sources) in expected)
        {
            AssertSameItems(sources, directives[name], $"{name} da CSP em {where} (\"{csp}\")");
        }
    }

    private static List<string> Split(string value, char separator) =>
        [.. value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static void AssertSameItems(IEnumerable<string> expected, IEnumerable<string> actual, string what)
    {
        var expectedSet = expected.Select(item => item.ToUpperInvariant()).Order(StringComparer.Ordinal).ToList();
        var actualSet = actual.Select(item => item.ToUpperInvariant()).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            expectedSet.SequenceEqual(actualSet),
            $"{what}: esperado [{string.Join(", ", expectedSet)}], veio [{string.Join(", ", actualSet)}].");
    }
}
