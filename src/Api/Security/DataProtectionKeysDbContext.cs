using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Vitrine.Api.Security;

/// <summary>
/// Anel de chaves do ASP.NET Core Data Protection no PostgreSQL (schema <c>security</c>, tabela
/// <c>data_protection_keys</c>); cada linha guarda o XML da chave cifrado pela chave mestra (SPEC-0008, ADR-0006).
/// </summary>
public sealed class DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public const string Schema = "security";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<DataProtectionKey>(key =>
        {
            key.ToTable("data_protection_keys");
            key.HasKey(k => k.Id);
            key.Property(k => k.Id).HasColumnName("id");
            key.Property(k => k.FriendlyName).HasColumnName("friendly_name");
            key.Property(k => k.Xml).HasColumnName("xml");
        });
    }
}

/// <summary>
/// Usado só pelo <c>dotnet ef migrations add</c> (sem banco real nem host):
/// <c>dnx dotnet-ef migrations add &lt;Nome&gt; --project src/Api --context DataProtectionKeysDbContext
/// --output-dir Security/Migrations</c>.
/// </summary>
internal sealed class DesignTimeDataProtectionKeysDbContextFactory : IDesignTimeDbContextFactory<DataProtectionKeysDbContext>
{
    public DataProtectionKeysDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DataProtectionKeysDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", DataProtectionKeysDbContext.ConfigureNpgsql)
            .Options);
}
