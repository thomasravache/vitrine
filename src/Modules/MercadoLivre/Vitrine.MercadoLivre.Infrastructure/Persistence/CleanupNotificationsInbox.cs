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
/// Apaga as notificações processadas há mais de <see cref="InboxNotification.Retention"/>; as não processadas nunca são
/// apagadas.
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

        var limit = clock.GetUtcNow() - InboxNotification.Retention;
        var deleted = await db.Notifications
            .Where(notification => notification.ProcessedAt != null && notification.ProcessedAt < limit)
            .ExecuteDeleteAsync(cancellationToken);
        Deleted(logger, deleted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Notificações do inbox processadas há mais de 7 dias apagadas: {Count}.")]
    private static partial void Deleted(ILogger logger, int count);
}
