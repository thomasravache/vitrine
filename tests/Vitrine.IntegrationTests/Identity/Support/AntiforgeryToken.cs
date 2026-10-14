using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace Vitrine.IntegrationTests.Identity.Support;

/// <summary>
/// Token antiforgery entregue por <c>GET /api/v1/auth/antiforgery</c> ("204 + cookie/cabeçalho de token").
/// Leitura do contrato: o token vem num cabeçalho de resposta (xsrf/csrf/antiforgery/RequestVerificationToken) e é
/// devolvido nos POST no cabeçalho de mesmo nome; ou, sem esse cabeçalho, num cookie legível pelo JavaScript
/// (<c>XSRF-TOKEN</c>/<c>CSRF-TOKEN</c>), devolvido em <c>X-&lt;nome do cookie&gt;</c> (convenção do padrão SPA).
/// </summary>
internal sealed partial record AntiforgeryToken(string HeaderName, string Value)
{
    public static AntiforgeryToken From(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var headers = response.Headers.Where(header => TokenHeaderName().IsMatch(header.Key)).ToList();
        if (headers.Count == 1)
        {
            return new AntiforgeryToken(headers[0].Key, headers[0].Value.Single());
        }

        var readableCookies = SetCookies.From(response)
            .Where(cookie => ReadableTokenCookieName().IsMatch(cookie.Name.Value ?? string.Empty) && !cookie.HttpOnly)
            .ToList();
        if (readableCookies.Count == 1)
        {
            var cookie = readableCookies[0];
            return new AntiforgeryToken($"X-{cookie.Name.Value}", Uri.UnescapeDataString(cookie.Value.Value ?? string.Empty));
        }

        Assert.Fail(
            "GET /api/v1/auth/antiforgery não entregou o token antiforgery: esperado exatamente um cabeçalho de token "
            + "(xsrf/csrf/antiforgery) ou um cookie legível XSRF-TOKEN/CSRF-TOKEN. Cabeçalhos: "
            + string.Join(", ", response.Headers.Select(header => header.Key)));
        return null!;
    }

    /// <summary>Mesmo cabeçalho, com valor adulterado (token inválido).</summary>
    public AntiforgeryToken Tampered() => this with { Value = "token-adulterado-" + Guid.NewGuid().ToString("N") };

    [GeneratedRegex("xsrf|csrf|antiforgery|request-?verification-?token", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenHeaderName();

    [GeneratedRegex("^(xsrf|csrf)[-_]?token$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadableTokenCookieName();
}

/// <summary>Leitura dos cabeçalhos <c>Set-Cookie</c> de uma resposta.</summary>
internal static partial class SetCookies
{
    public static IList<SetCookieHeaderValue> From(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues(HeaderNames.SetCookie, out var values)
            ? SetCookieHeaderValue.ParseList([.. values])
            : [];
    }

    /// <summary>Cookies de sessão: todo cookie definido que não é do mecanismo antiforgery.</summary>
    public static List<SetCookieHeaderValue> Session(HttpResponseMessage response) =>
        From(response).Where(cookie => !AntiforgeryCookieName().IsMatch(cookie.Name.Value ?? string.Empty)).ToList();

    /// <summary>Cookie expirado/removido (Expires no passado, Max-Age &lt;= 0 ou valor vazio).</summary>
    public static bool IsDeletion(SetCookieHeaderValue cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        return (cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow)
            || cookie.MaxAge is { } maxAge && maxAge <= TimeSpan.Zero
            || string.IsNullOrEmpty(cookie.Value.Value);
    }

    [GeneratedRegex("xsrf|csrf|antiforgery", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryCookieName();
}
