namespace Vitrine.MercadoLivre.Domain.Notifications;

/// <summary>
/// Notificação do ML gravada no inbox <c>mercadolivre.notifications</c> (SPEC-0023, ADR-0014): só os campos do contrato
/// (<c>_id</c>, <c>topic</c>, <c>resource</c>, <c>user_id</c>, <c>application_id</c>, <c>attempts</c>, <c>sent</c>,
/// <c>received</c>), aceita uma vez por <c>_id</c> e processada depois, com um <see cref="InboxNotificationOutcome"/>.
/// A gravação é feita pela persistência (INSERT com deduplicação); o domínio só marca o processamento.
/// </summary>
public sealed class InboxNotification
{
    /// <summary>Quanto tempo uma notificação processada com sucesso continua no inbox (SPEC-0023, §5).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Quanto tempo uma notificação processada como <c>unknown_user</c>/<c>invalid</c> continua no inbox (emenda v1.2).</summary>
    public static readonly TimeSpan RejectedRetention = TimeSpan.FromDays(1);

    /// <summary>Quanto tempo, desde o <c>accepted_at</c> (hora do servidor), uma notificação não processada continua no inbox (emenda v1.2).</summary>
    public static readonly TimeSpan UnprocessedRetention = TimeSpan.FromDays(7);

    // Materialização pelo EF Core.
    private InboxNotification()
    {
    }

    public Guid Id { get; private set; }

    /// <summary><c>_id</c> da notificação (único).</summary>
    public string NotificationId { get; private set; } = string.Empty;

    public string Topic { get; private set; } = string.Empty;

    public string Resource { get; private set; } = string.Empty;

    public long MlUserId { get; private set; }

    public long ApplicationId { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public InboxNotificationOutcome? Outcome { get; private set; }

    public bool IsProcessed => ProcessedAt is not null;

    /// <summary>Marca a notificação processada agora, com o desfecho.</summary>
    public void MarkProcessed(InboxNotificationOutcome outcome, DateTimeOffset now)
    {
        if (IsProcessed)
        {
            throw new InvalidOperationException("A notificação já foi processada.");
        }

        Outcome = outcome;
        ProcessedAt = now;
    }
}

/// <summary>Desfecho do processamento (coluna <c>outcome</c>; emenda v1.1 da SPEC-0023).</summary>
public enum InboxNotificationOutcome
{
    /// <summary><c>item_changed</c>: <c>MercadoLivreItemChanged</c> publicado.</summary>
    ItemChanged,

    /// <summary><c>unknown_user</c>: nenhuma conta com o <c>user_id</c>.</summary>
    UnknownUser,

    /// <summary><c>invalid</c>: <c>resource</c> fora do formato do tópico.</summary>
    Invalid,
}
