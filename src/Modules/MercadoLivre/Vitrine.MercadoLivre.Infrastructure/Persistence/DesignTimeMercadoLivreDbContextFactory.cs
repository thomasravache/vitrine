using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vitrine.Common.Security;

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
            .Options,
            new DesignTimeProtector());

    // As migrações só leem o modelo: nada é cifrado em tempo de design.
    private sealed class DesignTimeProtector : IFieldProtector
    {
        public string Protect(string plaintext, string purpose) => throw new NotSupportedException();

        public string Unprotect(string ciphertext, string purpose) => throw new NotSupportedException();
    }
}
