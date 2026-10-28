using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
