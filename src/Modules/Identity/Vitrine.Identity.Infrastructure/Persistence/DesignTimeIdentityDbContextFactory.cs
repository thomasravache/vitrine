using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vitrine.Identity.Infrastructure.Persistence;

/// <summary>
/// Usado só pelo <c>dotnet ef migrations add</c> (sem banco real nem host):
/// <c>dnx dotnet-ef migrations add &lt;Nome&gt; --project src/Modules/Identity/Vitrine.Identity.Infrastructure
/// --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class DesignTimeIdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityModuleDbContext>
{
    public IdentityModuleDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<IdentityModuleDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", IdentityModule.ConfigureNpgsql)
            .Options);
}
