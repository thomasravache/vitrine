using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Vitrine.IntegrationTests.Security.Support;

/// <summary>
/// Regra "autorização negada por padrão" (ADR-0007, contrato da SPEC-0008): todo endpoint mapeado exige usuário
/// autenticado — pela própria política ou pela política de fallback — exceto os da lista <see cref="Allowed"/>.
/// Um endpoint é anônimo quando tem <see cref="IAllowAnonymous"/> ou quando a política efetiva (a dele, ou a de
/// fallback se não tiver nenhuma) não contém <see cref="DenyAnonymousAuthorizationRequirement"/>.
/// </summary>
internal static class AnonymousEndpoints
{
    /// <summary>Endpoint permitido sem login: método HTTP (ou <c>null</c> para qualquer) e padrão de rota.</summary>
    public sealed record Entry(string? Method, string Pattern);

    /// <summary>
    /// A lista <c>AnonymousEndpoints</c> do contrato: cadastro, login, antiforgery, health checks e a SPA estática
    /// (o fallback para o <c>index.html</c>, mapeado só com a SPA no web root). Também anônimos, e conhecidos: os
    /// fallbacks de <c>/api/{**path}</c> e <c>/health/{**path}</c>, sempre mapeados, que só devolvem 404 (sem eles uma
    /// rota de API inexistente daria 401 ou o <c>index.html</c>). Entrada com método exige que o endpoint declare só
    /// esse método: endpoint sem metadado de método (aceita qualquer um) não cumpre a entrada.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Allowed =
    [
        new("POST", "/api/v1/auth/register"),
        new("POST", "/api/v1/auth/login"),
        new("GET", "/api/v1/auth/antiforgery"),
        new("GET", "/health/live"),
        new("GET", "/health/ready"),
        new(null, "{*path:nonfile}"),
        new(null, "/api/{**path}"),
        new(null, "/health/{**path}"),
    ];

    /// <summary>Endpoints anônimos fora da lista, como "MÉTODO padrão" (vazio = regra cumprida).</summary>
    public static async Task<IReadOnlyList<string>> ViolationsAsync(
        IEnumerable<Endpoint> endpoints, IAuthorizationPolicyProvider policyProvider)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policyProvider);

        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            if (await IsAnonymousAsync(endpoint, policyProvider) && !IsAllowed(endpoint))
            {
                violations.Add(Describe(endpoint));
            }
        }

        return violations;
    }

    /// <summary>A política de fallback é exatamente <c>RequireAuthenticatedUser</c> (sem exceções por caminho).</summary>
    public static bool IsRequireAuthenticatedUser(AuthorizationPolicy? policy) =>
        policy is not null
        && policy.Requirements.Count == 1
        && policy.Requirements[0] is DenyAnonymousAuthorizationRequirement;

    /// <summary>O padrão de rota está na lista (sem olhar o método): rotas que o IT-01 não chama esperando 401.</summary>
    public static bool IsListedPattern(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Allowed.Any(entry => string.Equals(entry.Pattern, endpoint.RoutePattern.RawText, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAllowed(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var methods = Methods(endpoint);
        return Allowed.Any(entry =>
            string.Equals(entry.Pattern, endpoint.RoutePattern.RawText, StringComparison.OrdinalIgnoreCase)
            && (entry.Method is null
                || (methods is not null && methods.All(method => string.Equals(method, entry.Method, StringComparison.OrdinalIgnoreCase)))));
    }

    public static IReadOnlyList<string>? Methods(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? [.. methods] : null;

    public static string Describe(RouteEndpoint endpoint) =>
        $"{(Methods(endpoint) is { } methods ? string.Join(',', methods) : "*")} {endpoint.RoutePattern.RawText}";

    private static async Task<bool> IsAnonymousAsync(Endpoint endpoint, IAuthorizationPolicyProvider policyProvider)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        var policy = await AuthorizationPolicy.CombineAsync(
                policyProvider,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
            ?? await policyProvider.GetFallbackPolicyAsync();

        return policy is null || !policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any();
    }
}
