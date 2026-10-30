using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Vitrine.Common.Web;

/// <summary>
/// Antiforgery nos POST/DELETE que mudam estado (ADR-0007): o cookie e o token devolvido no cabeçalho emitido por
/// <c>GET /api/v1/auth/antiforgery</c>; sem token válido, 400 <c>ANTIFORGERY_INVALID</c> e o endpoint não executa.
/// </summary>
public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            return TypedResults.Problem(
                title: "Token antiforgery ausente ou inválido.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = WebErrorCodes.AntiforgeryInvalid });
        }

        return await next(context);
    }
}
