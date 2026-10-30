using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vitrine.Common.Security;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Domain.Authorizations;
using Wolverine.EntityFrameworkCore.Internals;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// DbContext do módulo MercadoLivre: contas conectadas e autorizações OAuth pendentes, no schema <c>mercadolivre</c>
/// (ADR-0001, ADR-0011). Tokens e <c>code_verifier</c> são gravados cifrados pelo <see cref="IFieldProtector"/> do host
/// (propósito <see cref="MercadoLivreModule.TokensProtectionPurpose"/> — SPEC-0008).
/// </summary>
/// <remarks>
/// O protetor entra pelo construtor e o conversor é montado no modelo, cacheado por protetor
/// (<see cref="ProtectorModelCacheKeyFactory"/>): todos os hosts do processo compartilham um único provedor interno do
/// EF Core, em vez de um por protetor (<c>UseFieldProtection</c>), que estoura o limite de provedores quando vários hosts
/// sobem no mesmo processo (testes).
/// </remarks>
public sealed class MercadoLivreModuleDbContext(DbContextOptions<MercadoLivreModuleDbContext> options, IFieldProtector protector)
    : DbContext(options)
{
    public const string Schema = "mercadolivre";

    public DbSet<MercadoLivreAccount> Accounts => Set<MercadoLivreAccount>();

    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();

    /// <summary>Protetor dos campos cifrados (parte da chave do cache do modelo).</summary>
    internal IFieldProtector Protector => protector;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<MercadoLivreAccount>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(a => a.Id);
            account.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            account.Property(a => a.WorkspaceId).HasColumnName("workspace_id");
            account.Property(a => a.MlUserId).HasColumnName("ml_user_id");
            account.Property(a => a.Nickname).HasColumnName("nickname").HasMaxLength(100).IsRequired();
            account.Property(a => a.SiteId).HasColumnName("site_id").HasMaxLength(10).IsRequired();
            account.Property(a => a.Status).HasColumnName("status").HasMaxLength(30)
                .HasConversion(status => AccountStatusNames.ToName(status), name => AccountStatusNames.FromName(name));
            account.Property(a => a.AccessToken).HasColumnName("access_token").IsRequired().HasConversion(Protected());
            account.Property(a => a.RefreshToken).HasColumnName("refresh_token").IsRequired().HasConversion(Protected());
            account.Property(a => a.AccessTokenExpiresAt).HasColumnName("access_token_expires_at");
            account.Property(a => a.ConnectedAt).HasColumnName("connected_at");
            account.Property(a => a.UpdatedAt).HasColumnName("updated_at");
            account.Ignore(a => a.DomainEvents);
            account.HasIndex(a => a.MlUserId).IsUnique();
            account.HasIndex(a => a.WorkspaceId);
        });

        modelBuilder.Entity<PendingAuthorization>(authorization =>
        {
            authorization.ToTable("pending_authorizations");
            authorization.HasKey(p => p.Id);
            authorization.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            authorization.Property(p => p.WorkspaceId).HasColumnName("workspace_id");
            authorization.Property(p => p.UserId).HasColumnName("user_id");
            authorization.Property(p => p.StateHash).HasColumnName("state_hash").HasMaxLength(64).IsRequired();
            authorization.Property(p => p.CodeVerifier).HasColumnName("code_verifier").IsRequired().HasConversion(Protected());
            authorization.Property(p => p.ExpiresAt).HasColumnName("expires_at");
            authorization.Property(p => p.UsedAt).HasColumnName("used_at");

            // Concorrência otimista pela coluna de sistema xmin: duas conclusões simultâneas do mesmo state não
            // consomem a autorização duas vezes.
            authorization.Property<uint>("Version").IsRowVersion();
            authorization.HasIndex(p => p.StateHash).IsUnique();
            authorization.HasIndex(p => p.ExpiresAt);
        });
    }

    // Grava cifrado e lê decifrado (texto adulterado lança CryptographicException, nunca texto parcial).
    // O modelo cacheado guarda só o protetor (cópia local), nunca o contexto que o montou.
    private ValueConverter<string, string> Protected()
    {
        var fields = protector;
        return new(
            plaintext => fields.Protect(plaintext, MercadoLivreModule.TokensProtectionPurpose),
            ciphertext => fields.Unprotect(ciphertext, MercadoLivreModule.TokensProtectionPurpose));
    }
}

/// <summary>
/// Um modelo por (chave do Wolverine, protetor): cada host usa o conversor com o próprio <see cref="IFieldProtector"/>.
/// Decora a chave do Wolverine, que separa os modelos pelo schema de envelopes do host (o Wolverine também troca esta
/// fábrica no <c>AddDbContextWithWolverineIntegration</c>; esta é registrada depois e a inclui).
/// </summary>
internal sealed class ProtectorModelCacheKeyFactory : IModelCacheKeyFactory
{
    private readonly WolverineModelCacheKeyFactory _wolverine = new();

    public object Create(DbContext context, bool designTime) =>
        context is MercadoLivreModuleDbContext module
            ? (_wolverine.Create(context, designTime), module.Protector)
            : _wolverine.Create(context, designTime);
}

/// <summary>Valores de <c>accounts.status</c> (os mesmos do contrato da API).</summary>
internal static class AccountStatusNames
{
    public const string Connected = "connected";
    public const string ReauthorizationRequired = "reauthorization_required";

    public static string ToName(MercadoLivreAccountStatus status) => status switch
    {
        MercadoLivreAccountStatus.Connected => Connected,
        MercadoLivreAccountStatus.ReauthorizationRequired => ReauthorizationRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static MercadoLivreAccountStatus FromName(string name) => name switch
    {
        Connected => MercadoLivreAccountStatus.Connected,
        ReauthorizationRequired => MercadoLivreAccountStatus.ReauthorizationRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
