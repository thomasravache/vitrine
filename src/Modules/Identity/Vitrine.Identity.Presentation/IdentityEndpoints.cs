using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Application.CurrentUser;
using Vitrine.Identity.Application.Register;

namespace Vitrine.Identity.Presentation;

/// <summary>Endpoints do módulo Identity (contrato da SPEC-0006).</summary>
public static class IdentityEndpoints
{
    /// <summary>
    /// Mapeia <c>/api/v1/auth</c> (antiforgery, register, login, logout) e <c>GET /api/v1/me</c>.
    /// Tudo exige sessão (política de fallback do host — ADR-0007), exceto o que é marcado com <c>AllowAnonymous</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var auth = endpoints.MapGroup("/api/v1/auth").WithTags("Auth");
        auth.MapGet("/antiforgery", IssueAntiforgeryToken).WithName("GetAntiforgeryToken").AllowAnonymous();
        auth.MapPost("/register", Register).WithName("Register").AllowAnonymous()
            .AddEndpointFilter<SignupOpenFilter>()
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        auth.MapPost("/login", Login).WithName("Login").AllowAnonymous()
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status423Locked);
        auth.MapPost("/logout", Logout).WithName("Logout")
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        endpoints.MapGet("/api/v1/me", GetMe).WithName("GetMe").WithTags("Me")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    /// <summary>
    /// Emite o par cookie + token antiforgery; o token vai no cabeçalho configurado no host
    /// (<see cref="AntiforgeryOptions.HeaderName"/>), em que os POST o devolvem.
    /// </summary>
    private static NoContent IssueAntiforgeryToken(HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> options)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers[options.Value.HeaderName ?? "X-XSRF-TOKEN"] = tokens.RequestToken;
        return TypedResults.NoContent();
    }

    private static async Task<Results<Created<RegisterResponse>, ProblemHttpResult>> Register(
        RegisterRequest request, RegisterCommandHandler handler, ISessions sessions, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RegisterCommand(request.Name, request.Email, request.Password), cancellationToken);
        switch (result)
        {
            case RegisterResult.Registered registered:
                await sessions.SignInAsync(registered.UserId, cancellationToken);
                return TypedResults.Created("/api/v1/me", new RegisterResponse(registered.UserId, registered.WorkspaceId));
            case RegisterResult.Invalid invalid:
                return Problems.ValidationFailed(invalid.Errors);
            default:
                return Problems.EmailTaken();
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Login(
        LoginRequest request, ISessions sessions, CancellationToken cancellationToken) =>
        await sessions.SignInWithPasswordAsync(request.Email, request.Password, cancellationToken) switch
        {
            LoginResult.SignedIn => TypedResults.NoContent(),
            LoginResult.LockedOut locked => Problems.LockedOut(locked.RetryAfter),
            _ => Problems.InvalidCredentials(),
        };

    private static async Task<NoContent> Logout(ISessions sessions)
    {
        await sessions.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMe(
        ClaimsPrincipal principal, IMeQueries queries, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || await queries.FindAsync(userId, cancellationToken) is not { } me)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(new MeResponse(
            me.UserId, me.Name, me.Email, new MeWorkspace(me.Workspace.Id, me.Workspace.Name, me.Workspace.Role)));
    }
}
