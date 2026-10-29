using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;

namespace Vitrine.Api.Security;

/// <summary>
/// Migrações do schema <c>security</c> (tabela de chaves do Data Protection) como recurso com estado (JasperFx), como as
/// dos módulos: aplicadas só pelo modo <c>--migrate</c>, pelo host de teste e no desenvolvimento local (ADR-0011).
/// </summary>
internal sealed class DataProtectionMigrations(IServiceScopeFactory scopes)
    : SystemPartBase("Chaves do Data Protection (EF Core)", new Uri("efcore://security"))
{
    public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources() =>
        new([new MigrationsResource(scopes)]);

    private sealed class MigrationsResource(IServiceScopeFactory scopes)
        : StatefulResourceBase("EF Core migrations", DataProtectionKeysDbContext.Schema, new Uri("efcore://security"), new Uri("postgresql://security"))
    {
        public override async Task Setup(CancellationToken token)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();
            await db.Database.MigrateAsync(token);
        }
    }
}
