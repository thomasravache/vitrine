using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Vitrine.MercadoLivre.Presentation;

/// <summary>Erros do módulo em application/problem+json, sempre com o campo <c>code</c> (contrato da SPEC-0020).</summary>
internal static class Problems
{
    public static ProblemHttpResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors) =>
        TypedResults.Problem(
            title: "Dados inválidos.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = MercadoLivreErrorCodes.ValidationFailed,
                ["errors"] = errors,
            });

    public static ProblemHttpResult Forbidden() =>
        Problem(StatusCodes.Status403Forbidden, MercadoLivreErrorCodes.Forbidden, "Só o dono do workspace pode fazer isso.");

    public static ProblemHttpResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, MercadoLivreErrorCodes.NotFound, "Conta não encontrada.");

    public static ProblemHttpResult NotConfigured() =>
        Problem(StatusCodes.Status503ServiceUnavailable, MercadoLivreErrorCodes.NotConfigured, "Integração com o Mercado Livre não configurada.");

    public static ProblemHttpResult StateInvalid() =>
        Problem(StatusCodes.Status400BadRequest, MercadoLivreErrorCodes.StateInvalid, "Autorização inválida ou expirada. Conecte a conta de novo.");

    public static ProblemHttpResult CodeInvalid() =>
        Problem(StatusCodes.Status400BadRequest, MercadoLivreErrorCodes.CodeInvalid, "O Mercado Livre recusou a autorização. Conecte a conta de novo.");

    public static ProblemHttpResult Unavailable() =>
        Problem(StatusCodes.Status502BadGateway, MercadoLivreErrorCodes.Unavailable, "O Mercado Livre não respondeu. Tente conectar de novo.");

    public static ProblemHttpResult SiteNotSupported() =>
        Problem(StatusCodes.Status422UnprocessableEntity, MercadoLivreErrorCodes.SiteNotSupported, "Só contas do Mercado Livre Brasil são aceitas.");

    public static ProblemHttpResult AccountInUse() =>
        Problem(StatusCodes.Status409Conflict, MercadoLivreErrorCodes.AccountInUse, "Esta conta do Mercado Livre já está conectada em outro workspace.");

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
