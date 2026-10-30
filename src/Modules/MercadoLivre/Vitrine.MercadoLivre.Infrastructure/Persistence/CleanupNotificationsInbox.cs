using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vitrine.MercadoLivre.Domain.Notifications;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// Limpeza diária do inbox das notificações (SPEC-0023, emenda v1.1): mensagem recorrente do Wolverine (ADR-0014),
/// agendada por <see cref="MercadoLivreModule.CleanupSchedule"/> e tratada por <see cref="CleanupNotificationsInboxHandler"/>.
/// </summary>
public sealed record CleanupNotificationsInbox;

/// <summary>
/// Apaga do inbox (emenda v1.2): as processadas com sucesso há mais de <see cref="InboxNotification.Retention"/>, as
/// <c>unknown_user</c>/<c>invalid</c> processadas há mais de <see cref="InboxNotification.RejectedRetention"/> e as não
/// processadas (retentativas esgotadas) aceitas (<c>accepted_at</c>, hora do servidor) há mais de <see cref="InboxNotification.UnprocessedRetention"/>.
/// </summary>
public static partial class CleanupNotificationsInboxHandler
{
    public static async Task Handle(
        CleanupNotificationsInbox message,
        MercadoLivreModuleDbContext db,
        TimeProvider clock,
        ILogger<CleanupNotificationsInbox> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow();
        var succeededBefore = now - InboxNotification.Retention;
        var rejectedBefore = now - InboxNotification.RejectedRetention;
        var unprocessedBefore = now - InboxNotification.UnprocessedRetention;
        var deleted = await db.Notifications
            .Where(notification =>
                (notification.ProcessedAt != null
                    && notification.Outcome == InboxNotificationOutcome.ItemChanged
                    && notification.ProcessedAt < succeededBefore)
                || (notification.ProcessedAt != null
                    && notification.Outcome != InboxNotificationOutcome.ItemChanged
                    && notification.ProcessedAt < rejectedBefore)
                || (notification.ProcessedAt == null && notification.AcceptedAt < unprocessedBefore))
            .ExecuteDeleteAsync(cancellationToken);
        Deleted(logger, deleted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Notificações apagadas do inbox pela retenção: {Count}.")]
    private static partial void Deleted(ILogger logger, int count);
}
