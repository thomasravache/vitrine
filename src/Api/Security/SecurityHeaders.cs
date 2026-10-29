namespace Vitrine.Api.Security;

/// <summary>
/// Cabeçalhos de segurança do contrato da SPEC-0008 em toda resposta da Api e da SPA, inclusive erros (gravados no
/// início da resposta, depois de qualquer limpeza do tratamento de exceções). HSTS só fora de Development e em
/// requisições HTTPS (atrás do Caddy, via forwarded headers), nunca para localhost.
/// </summary>
internal sealed class SecurityHeaders
{
    public const string HstsValue = "max-age=31536000; includeSubDomains";

    private static readonly string[] HstsExcludedHosts = ["localhost", "127.0.0.1", "[::1]"];

    private readonly string _contentSecurityPolicy;
    private readonly bool _hsts;

    public SecurityHeaders(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        _contentSecurityPolicy = ContentSecurityPolicy(SentryOrigin(configuration[ObservabilitySentryDsnKey]));
        _hsts = !environment.IsDevelopment();
    }

    // Mesma chave que a observabilidade usa para ligar o Sentry (SPEC-0007): backend e SPA usam o mesmo projeto.
    private const string ObservabilitySentryDsnKey = Observability.ObservabilityExtensions.SentryDsnKey;

    public string ContentSecurityPolicyValue => _contentSecurityPolicy;

    /// <summary>Política da SPA: tudo da própria origem; <c>connect-src</c> também a ingestão do Sentry, se configurado.</summary>
    public static string ContentSecurityPolicy(string? sentryOrigin) =>
        $"default-src 'self'; script-src 'self'; connect-src 'self'{(sentryOrigin is null ? string.Empty : " " + sentryOrigin)}; frame-ancestors 'none'";

    /// <summary>Origem (esquema://host[:porta]) do DSN do Sentry; <c>null</c> sem DSN ou com DSN inválido.</summary>
    public static string? SentryOrigin(string? dsn) =>
        !string.IsNullOrWhiteSpace(dsn)
        && Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? $"{uri.Scheme}://{uri.Authority}"
            : null;

    public void Apply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = _contentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers.XFrameOptions = "DENY";
        headers.Remove("Server");
        headers.Remove("X-Powered-By");

        if (_hsts && request.IsHttps && !HstsExcludedHosts.Contains(request.Host.Host, StringComparer.OrdinalIgnoreCase))
        {
            headers.StrictTransportSecurity = HstsValue;
        }
    }
}

internal static class SecurityHeadersMiddleware
{
    /// <summary>Registra a gravação dos cabeçalhos no início de toda resposta.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var securityHeaders = app.ApplicationServices.GetRequiredService<SecurityHeaders>();
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(
                static state =>
                {
                    var (headers, httpContext) = ((SecurityHeaders, HttpContext))state;
                    headers.Apply(httpContext);
                    return Task.CompletedTask;
                },
                (securityHeaders, context));
            return next(context);
        });
    }
}
