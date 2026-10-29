using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Vitrine.Identity.Presentation;

/// <summary>
/// Antiforgery nos POST que mudam estado (ADR-0007): o cookie e o token devolvido no cabeçalho emitido por
/// <c>GET /api/v1/auth/antiforgery</c>; sem token válido, 400 e o endpoint não executa.
/// </summary>
internal sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            return Problems.AntiforgeryInvalid();
        }

        return await next(context);
    }
}

/// <summary>
/// Flag <c>Features:SignupOpen</c>: com o cadastro fechado a rota responde 404, como se não existisse.
/// Lida a cada requisição (pode mudar sem novo deploy).
/// </summary>
internal sealed class SignupOpenFilter(IConfiguration configuration) : IEndpointFilter
{
    public const string FlagKey = "Features:SignupOpen";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        configuration.GetValue<bool>(FlagKey) ? await next(context) : Problems.NotFound();
}

/// <summary>
/// Endpoints de auth só em HTTPS quando o cookie antiforgery é <c>Secure=Always</c> (fora de Development — SPEC-0008):
/// requisição que chega em HTTP (fora do proxy, ou com <c>X-Forwarded-Proto</c> de IP não confiável, que é ignorado)
/// recebe 400 em vez da falha interna do antiforgery.
/// </summary>
internal sealed class HttpsRequiredFilter(IOptions<AntiforgeryOptions> antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.IsHttps || antiforgery.Value.Cookie.SecurePolicy != CookieSecurePolicy.Always
            ? await next(context)
            : Problems.HttpsRequired();
}
