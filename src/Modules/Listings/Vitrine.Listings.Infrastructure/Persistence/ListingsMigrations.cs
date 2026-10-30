using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Vitrine.Listings.Infrastructure.Persistence;

/// <summary>
/// Migrações do schema <c>listings</c> expostas como recurso com estado (JasperFx): aplicadas só por quem chama
/// <c>SetupResources</c> — o modo <c>--migrate</c> do host no pipeline, o host de teste e o desenvolvimento local —,
/// nunca no startup normal de produção (ADR-0011).
/// </summary>
internal sealed class ListingsMigrations(IServiceScopeFactory scopes)
    : SystemPartBase("Módulo Listings (EF Core)", new Uri("efcore://listings"))
{
    public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources() =>
        new([new MigrationsResource(scopes)]);

    private sealed class MigrationsResource(IServiceScopeFactory scopes)
        : StatefulResourceBase("EF Core migrations", ListingsModuleDbContext.Schema, new Uri("efcore://listings"), new Uri("postgresql://listings"))
    {
        public override async Task Setup(CancellationToken token)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ListingsModuleDbContext>();
            await db.Database.MigrateAsync(token);
        }
    }
}
