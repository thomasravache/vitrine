using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// Usado só pelo <c>dotnet ef migrations add</c> (sem banco real nem host):
/// <c>dnx dotnet-ef migrations add &lt;Nome&gt; --project src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure
/// --output-dir Persistence/Migrations</c>.
/// </summary>
internal sealed class DesignTimeMercadoLivreDbContextFactory : IDesignTimeDbContextFactory<MercadoLivreModuleDbContext>
{
    public MercadoLivreModuleDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MercadoLivreModuleDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", MercadoLivreModule.ConfigureNpgsql)
            .Options);
}
