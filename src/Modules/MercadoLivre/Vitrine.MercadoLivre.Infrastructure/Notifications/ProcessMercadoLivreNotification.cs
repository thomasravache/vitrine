using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Application.Notifications;
using Vitrine.MercadoLivre.Domain.Notifications;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Vitrine.MercadoLivre.Infrastructure.Notifications;

/// <summary>Processamento de uma notificação aceita no inbox (fila local durável — SPEC-0023, §6).</summary>
/// <param name="InboxId">Id da linha em <c>mercadolivre.notifications</c>.</param>
public sealed record ProcessMercadoLivreNotification(Guid InboxId);

/// <summary>
/// Processa a notificação do inbox: localiza a conta pelo <c>user_id</c> (sem conta: <c>unknown_user</c>) e, no tópico
/// <c>items</c> com <c>resource</c> <c>/items/MLB…</c>, publica <see cref="MercadoLivreItemChanged"/> no outbox, na
/// mesma transação que marca a linha processada (<c>item_changed</c>); outro resource é <c>invalid</c>, sem evento.
/// Idempotente: linha já processada (ou apagada) é ignorada. Falhas são retentadas pelo Wolverine.
/// </summary>
public static class ProcessMercadoLivreNotificationHandler
{
    /// <summary>Falha de banco: novas tentativas imediatas e, depois, retentativas duráveis agendadas.</summary>
    public static void Configure(HandlerChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        chain.OnException<DbException>().Or<DbUpdateException>().Or<TimeoutException>()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
            .Then.ScheduleRetry(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    }

    public static async Task Handle(
        ProcessMercadoLivreNotification message,
        IDbContextOutbox<MercadoLivreModuleDbContext> outbox,
        INotificationMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(clock);

        var db = outbox.DbContext;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Travada na transação: duas entregas da mesma mensagem não publicam o evento duas vezes.
        var rows = await db.Notifications
            .FromSql($"select * from mercadolivre.notifications where id = {message.InboxId} for update")
            .ToListAsync(cancellationToken);
        if (rows.Count == 0 || rows[0].IsProcessed)
        {
            return;
        }

        var notification = rows[0];
        var account = await db.Accounts.AsNoTracking()
            .Where(candidate => candidate.MlUserId == notification.MlUserId)
            .Select(candidate => new { candidate.Id, candidate.WorkspaceId })
            .FirstOrDefaultAsync(cancellationToken);

        InboxNotificationOutcome outcome;
        if (account is null)
        {
            outcome = InboxNotificationOutcome.UnknownUser;
        }
        else if (!string.Equals(notification.Topic, ReceiveNotificationHandler.ItemsTopic, StringComparison.Ordinal)
            || !NotificationResource.TryGetItemId(notification.Resource, out var itemId))
        {
            outcome = InboxNotificationOutcome.Invalid;
        }
        else
        {
            await outbox.PublishAsync(new MercadoLivreItemChanged(
                account.Id, account.WorkspaceId, notification.MlUserId, itemId, notification.NotificationId, notification.SentAt));
            outcome = InboxNotificationOutcome.ItemChanged;
        }

        notification.MarkProcessed(outcome, clock.GetUtcNow());
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

        switch (outcome)
        {
            case InboxNotificationOutcome.UnknownUser:
                metrics.Record(notification.Topic, NotificationResult.UnknownUser);
                break;
            case InboxNotificationOutcome.Invalid:
                metrics.Record(notification.Topic, NotificationResult.Invalid);
                break;
        }
    }
}
