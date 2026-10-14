using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Vitrine.Identity.Presentation;

/// <summary>Endpoints do módulo Identity (contrato da SPEC-0006).</summary>
public static class IdentityEndpoints
{
    /// <summary>
    /// Mapeia <c>/api/v1/auth</c> (antiforgery, register, login, logout) e <c>GET /api/v1/me</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // SPEC-0006: rotas do contrato ainda sem implementação (501).
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapGet("/antiforgery", NotImplemented);
        auth.MapPost("/register", NotImplemented);
        auth.MapPost("/login", NotImplemented);
        auth.MapPost("/logout", NotImplemented);
        endpoints.MapGet("/api/v1/me", NotImplemented);

        return endpoints;
    }

    private static IResult NotImplemented() => Results.StatusCode(StatusCodes.Status501NotImplemented);
}
