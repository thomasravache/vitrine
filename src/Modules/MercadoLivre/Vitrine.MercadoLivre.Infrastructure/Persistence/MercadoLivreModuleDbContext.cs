using Microsoft.EntityFrameworkCore;
using Vitrine.Common.Security;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Domain.Authorizations;
using Vitrine.MercadoLivre.Domain.Notifications;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// DbContext do módulo MercadoLivre: contas conectadas, autorizações OAuth pendentes e o inbox das notificações do ML
/// (SPEC-0023), no schema <c>mercadolivre</c>
/// (ADR-0001, ADR-0011). Tokens e <c>code_verifier</c> são gravados cifrados (<c>IsProtected</c> com o
/// <see cref="IFieldProtector"/> ligado por <c>UseFieldProtection</c>, propósito
/// <see cref="MercadoLivreModule.TokensProtectionPurpose"/> — SPEC-0008, SPEC-0026).
/// </summary>
public sealed class MercadoLivreModuleDbContext(DbContextOptions<MercadoLivreModuleDbContext> options) : DbContext(options)
{
    public const string Schema = "mercadolivre";

    public DbSet<MercadoLivreAccount> Accounts => Set<MercadoLivreAccount>();

    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();

    public DbSet<InboxNotification> Notifications => Set<InboxNotification>();

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
            account.Property(a => a.AccessToken).HasColumnName("access_token").IsRequired()
                .IsProtected(MercadoLivreModule.TokensProtectionPurpose);
            account.Property(a => a.RefreshToken).HasColumnName("refresh_token").IsRequired()
                .IsProtected(MercadoLivreModule.TokensProtectionPurpose);
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
            authorization.Property(p => p.CodeVerifier).HasColumnName("code_verifier").IsRequired()
                .IsProtected(MercadoLivreModule.TokensProtectionPurpose);
            authorization.Property(p => p.ExpiresAt).HasColumnName("expires_at");
            authorization.Property(p => p.UsedAt).HasColumnName("used_at");

            // Concorrência otimista pela coluna de sistema xmin: duas conclusões simultâneas do mesmo state não
            // consomem a autorização duas vezes.
            authorization.Property<uint>("Version").IsRowVersion();
            authorization.HasIndex(p => p.StateHash).IsUnique();
            authorization.HasIndex(p => p.ExpiresAt);
        });

        // Inbox do webhook (SPEC-0023, §6): dedupe pelo _id (notification_id UNIQUE; INSERT ... ON CONFLICT DO NOTHING).
        modelBuilder.Entity<InboxNotification>(notification =>
        {
            notification.ToTable("notifications");
            notification.HasKey(n => n.Id);
            notification.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
            notification.Property(n => n.NotificationId).HasColumnName("notification_id").HasMaxLength(64).IsRequired();
            notification.Property(n => n.Topic).HasColumnName("topic").HasMaxLength(64).IsRequired();
            notification.Property(n => n.Resource).HasColumnName("resource").IsRequired();
            notification.Property(n => n.MlUserId).HasColumnName("ml_user_id");
            notification.Property(n => n.ApplicationId).HasColumnName("application_id");
            notification.Property(n => n.Attempts).HasColumnName("attempts");
            notification.Property(n => n.SentAt).HasColumnName("sent_at");
            notification.Property(n => n.ReceivedAt).HasColumnName("received_at");
            notification.Property(n => n.AcceptedAt).HasColumnName("accepted_at");
            notification.Property(n => n.ProcessedAt).HasColumnName("processed_at");
            notification.Property(n => n.Outcome).HasColumnName("outcome").HasMaxLength(20)
                .HasConversion(
                    outcome => outcome == null ? null : NotificationOutcomeNames.ToName(outcome.Value),
                    name => name == null ? null : NotificationOutcomeNames.FromName(name));
            notification.Ignore(n => n.IsProcessed);
            notification.HasIndex(n => n.NotificationId).IsUnique();
            notification.HasIndex(n => n.ProcessedAt);
        });
    }

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

/// <summary>Valores de <c>notifications.outcome</c> (emenda v1.1 da SPEC-0023).</summary>
internal static class NotificationOutcomeNames
{
    public const string ItemChanged = "item_changed";
    public const string UnknownUser = "unknown_user";
    public const string Invalid = "invalid";

    public static string ToName(InboxNotificationOutcome outcome) => outcome switch
    {
        InboxNotificationOutcome.ItemChanged => ItemChanged,
        InboxNotificationOutcome.UnknownUser => UnknownUser,
        InboxNotificationOutcome.Invalid => Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    public static InboxNotificationOutcome FromName(string name) => name switch
    {
        ItemChanged => InboxNotificationOutcome.ItemChanged,
        UnknownUser => InboxNotificationOutcome.UnknownUser,
        Invalid => InboxNotificationOutcome.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
