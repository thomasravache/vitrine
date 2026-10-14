using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Vitrine.Identity.Infrastructure.Persistence;

/// <summary>
/// Migrações do schema <c>identity</c> expostas como recurso com estado (JasperFx): aplicadas só por quem chama
/// <c>SetupResources</c> — o modo <c>--migrate</c> do host no pipeline, o host de teste e o desenvolvimento local —,
/// nunca no startup normal de produção (ADR-0011).
/// </summary>
internal sealed class IdentityMigrations(IServiceScopeFactory scopes)
    : SystemPartBase("Módulo Identity (EF Core)", new Uri("efcore://identity"))
{
    public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources() =>
        new([new MigrationsResource(scopes)]);

    private sealed class MigrationsResource(IServiceScopeFactory scopes)
        : StatefulResourceBase("EF Core migrations", IdentityModuleDbContext.Schema, new Uri("efcore://identity"), new Uri("postgresql://identity"))
    {
        public override async Task Setup(CancellationToken token)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityModuleDbContext>();
            await db.Database.MigrateAsync(token);
        }
    }
}
