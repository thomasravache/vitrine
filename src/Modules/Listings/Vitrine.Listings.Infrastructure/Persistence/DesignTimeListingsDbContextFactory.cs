using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vitrine.Listings.Infrastructure.Persistence;

/// <summary>
/// Usado só pelo <c>dotnet ef migrations add</c> (sem banco real nem host):
/// <c>dnx dotnet-ef migrations add &lt;Nome&gt; --project src/Modules/Listings/Vitrine.Listings.Infrastructure
/// --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class DesignTimeListingsDbContextFactory : IDesignTimeDbContextFactory<ListingsModuleDbContext>
{
    public ListingsModuleDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ListingsModuleDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", ListingsModule.ConfigureNpgsql)
            .Options);
}
