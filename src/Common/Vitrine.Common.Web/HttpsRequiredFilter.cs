using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Vitrine.Common.Web;

/// <summary>
/// Endpoints que dependem do cookie antiforgery só em HTTPS quando ele é <c>Secure=Always</c> (fora de Development —
/// SPEC-0008): requisição que chega em HTTP (fora do proxy, ou com <c>X-Forwarded-Proto</c> de IP não confiável, que é
/// ignorado) recebe 400 em vez da falha interna do antiforgery.
/// </summary>
public sealed class HttpsRequiredFilter(IOptions<AntiforgeryOptions> antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Sem code próprio: o host acrescenta o genérico (BAD_REQUEST); não é um erro do contrato da SPA.
        return context.HttpContext.Request.IsHttps || antiforgery.Value.Cookie.SecurePolicy != CookieSecurePolicy.Always
            ? await next(context)
            : TypedResults.Problem(title: "HTTPS obrigatório.", statusCode: StatusCodes.Status400BadRequest);
    }
}
