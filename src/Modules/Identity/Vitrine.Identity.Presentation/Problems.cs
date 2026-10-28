using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Vitrine.Identity.Presentation;

/// <summary>Erros do módulo em application/problem+json, sempre com o campo <c>code</c> (contrato da SPEC-0006).</summary>
internal static class Problems
{
    public static ProblemHttpResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors) =>
        TypedResults.Problem(
            title: "Dados inválidos.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = IdentityErrorCodes.ValidationFailed,
                ["errors"] = errors,
            });

    public static ProblemHttpResult EmailTaken() =>
        Problem(StatusCodes.Status409Conflict, IdentityErrorCodes.EmailTaken, "Este e-mail já tem uma conta.");

    public static ProblemHttpResult InvalidCredentials() =>
        Problem(StatusCodes.Status401Unauthorized, IdentityErrorCodes.InvalidCredentials, "E-mail ou senha incorretos.");

    public static ProblemHttpResult LockedOut(TimeSpan retryAfter) =>
        TypedResults.Problem(
            title: "Muitas tentativas. Tente de novo mais tarde.",
            statusCode: StatusCodes.Status423Locked,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = IdentityErrorCodes.LockedOut,
                ["retryAfterSeconds"] = (int)Math.Ceiling(retryAfter.TotalSeconds),
            });

    public static ProblemHttpResult AntiforgeryInvalid() =>
        Problem(StatusCodes.Status400BadRequest, IdentityErrorCodes.AntiforgeryInvalid, "Token antiforgery ausente ou inválido.");

    public static ProblemHttpResult NotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
