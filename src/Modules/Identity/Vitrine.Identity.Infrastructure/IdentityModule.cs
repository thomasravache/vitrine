using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vitrine.Identity.Infrastructure;

/// <summary>Registro do módulo Identity no host (ASP.NET Core Identity, DbContext do schema "identity", outbox).</summary>
public static class IdentityModule
{
    /// <summary>Registra os serviços do módulo Identity.</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // SPEC-0006: ainda não implementado (contrato apenas).
        return services;
    }
}
