namespace Vitrine.Identity.Presentation;

/// <summary>Corpo de <c>POST /api/v1/auth/register</c>.</summary>
public sealed record RegisterRequest(string Name, string Email, string Password);

/// <summary>Resposta 201 de <c>POST /api/v1/auth/register</c>.</summary>
public sealed record RegisterResponse(Guid UserId, Guid WorkspaceId);

/// <summary>Corpo de <c>POST /api/v1/auth/login</c>.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Resposta 200 de <c>GET /api/v1/me</c>.</summary>
public sealed record MeResponse(Guid UserId, string Name, string Email, MeWorkspace Workspace);

/// <summary>Workspace do usuário autenticado em <c>GET /api/v1/me</c> (<c>role</c>: <c>Owner</c> | <c>Member</c>).</summary>
public sealed record MeWorkspace(Guid Id, string Name, string Role);

/// <summary>Valores do campo <c>code</c> dos erros (application/problem+json) do módulo Identity.</summary>
public static class IdentityErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string LockedOut = "LOCKED_OUT";
    public const string AntiforgeryInvalid = "ANTIFORGERY_INVALID";
}
