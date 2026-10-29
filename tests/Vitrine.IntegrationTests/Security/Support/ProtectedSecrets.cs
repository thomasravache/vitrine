using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vitrine.Common.Security;

namespace Vitrine.IntegrationTests.Security.Support;

/// <summary>Entidade só de teste com um campo sensível protegido (como o token do Mercado Livre dos próximos épicos).</summary>
internal sealed class StoredSecret
{
    public Guid Id { get; set; }

    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// Contexto EF Core de teste no schema <c>security_tests</c>, com <see cref="StoredSecret.Secret"/> marcado com
/// <c>IsProtected(<see cref="Purpose"/>)</c> e o <see cref="IFieldProtector"/> do host ligado por
/// <c>UseFieldProtection</c>.
/// </summary>
internal sealed class ProtectedSecretsContext(string connectionString, IFieldProtector protector) : DbContext
{
    public const string Purpose = "teste-segredo";
    public const string Schema = "security_tests";
    public const string Table = "stored_secrets";

    public DbSet<StoredSecret> Secrets => Set<StoredSecret>();

    /// <summary>Cria a tabela de teste no banco (que já tem os schemas da Api).</summary>
    public async Task CreateTableAsync(CancellationToken cancellationToken) =>
        await this.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);

    /// <summary>Valor bruto da coluna, lido por SQL cru (sem EF nem conversor).</summary>
    public static async Task<string> ReadRawAsync(string connectionString, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"select secret from {Schema}.{Table} where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseNpgsql(connectionString).UseFieldProtection(protector);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<StoredSecret>(secret =>
        {
            secret.ToTable(Table);
            secret.Property(x => x.Id).HasColumnName("id");
            secret.Property(x => x.Secret).HasColumnName("secret").IsProtected(Purpose);
        });
    }
}
