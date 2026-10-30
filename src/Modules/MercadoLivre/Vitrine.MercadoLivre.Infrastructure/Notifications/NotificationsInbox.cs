using Microsoft.EntityFrameworkCore;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Application.Notifications;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.MercadoLivre.Infrastructure.Notifications;

/// <summary>
/// Inbox <c>mercadolivre.notifications</c> (SPEC-0023, ADR-0014): um INSERT com <c>ON CONFLICT (notification_id) DO
/// NOTHING</c> e, só quando a linha é nova, <see cref="ProcessMercadoLivreNotification"/> no outbox da mesma transação.
/// </summary>
internal sealed class NotificationsInbox(IDbContextOutbox<MercadoLivreModuleDbContext> outbox) : INotificationsInbox
{
    public async Task<bool> TryAcceptAsync(MercadoLivreNotification notification, DateTimeOffset acceptedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var db = outbox.DbContext;
        var id = Guid.CreateVersion7(acceptedAt);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            insert into mercadolivre.notifications
                (id, notification_id, topic, resource, ml_user_id, application_id, attempts, sent_at, received_at, accepted_at)
            values ({id}, {notification.NotificationId}, {notification.Topic}, {notification.Resource}, {notification.MlUserId},
                    {notification.ApplicationId}, {notification.Attempts}, {notification.Sent}, {notification.Received}, {acceptedAt})
            on conflict (notification_id) do nothing
            """,
            cancellationToken);
        if (inserted == 0)
        {
            // Já gravada (retentativa do ML): nada novo; a transação é desfeita no descarte.
            return false;
        }

        await outbox.PublishAsync(new ProcessMercadoLivreNotification(id));
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
        return true;
    }
}
