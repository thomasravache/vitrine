using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Vitrine.Identity.Presentation;

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
