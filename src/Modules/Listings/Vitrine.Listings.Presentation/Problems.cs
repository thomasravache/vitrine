using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Vitrine.Listings.Presentation;

/// <summary>Erros do módulo em application/problem+json, sempre com o campo <c>code</c> (contrato da SPEC-0022).</summary>
internal static class Problems
{
    public static ProblemHttpResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors) =>
        TypedResults.Problem(
            title: "Dados inválidos.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ListingsErrorCodes.ValidationFailed,
                ["errors"] = errors,
            });

    public static ProblemHttpResult Forbidden() =>
        Problem(StatusCodes.Status403Forbidden, ListingsErrorCodes.Forbidden, "Você não pertence a nenhum workspace.");

    public static ProblemHttpResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, ListingsErrorCodes.NotFound, "Conta não encontrada.");

    public static ProblemHttpResult ReauthorizationRequired() =>
        Problem(
            StatusCodes.Status409Conflict,
            ListingsErrorCodes.ReauthorizationRequired,
            "A conta precisa ser reconectada ao Mercado Livre antes de sincronizar.");

    public static ProblemHttpResult SyncTooSoon(int retryAfterSeconds) =>
        TypedResults.Problem(
            title: "A conta foi sincronizada há pouco. Tente de novo em instantes.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ListingsErrorCodes.SyncTooSoon,
                ["retryAfterSeconds"] = retryAfterSeconds,
            });

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
