using Vitrine.MercadoLivre.Application.Abstractions;

namespace Vitrine.MercadoLivre.Application.Notifications;

/// <summary>
/// Recebimento do webhook (SPEC-0023, §6): só validação barata e a gravação no inbox; o processamento (conta, resource e
/// evento) é durável e acontece depois. <c>application_id</c> de outro app e tópicos não tratados são descartados sem
/// gravar nada.
/// </summary>
public sealed class ReceiveNotificationHandler(
    IMercadoLivreApplication application,
    INotificationsInbox inbox,
    INotificationMetrics metrics,
    TimeProvider clock)
{
    /// <summary>Tópico de anúncios, o único tratado (SPEC-0023).</summary>
    public const string ItemsTopic = "items";

    public async Task<ReceiveNotificationResult> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (application.ApplicationId is not { } expectedApplicationId)
        {
            return new ReceiveNotificationResult.NotConfigured();
        }

        MercadoLivreNotification notification;
        switch (NotificationPayload.Parse(body.Span))
        {
            case NotificationPayloadResult.Valid valid:
                notification = valid.Notification;
                break;

            case NotificationPayloadResult.Invalid invalid:
                metrics.Record(topic: null, NotificationResult.Invalid);
                return new ReceiveNotificationResult.Invalid(invalid.Errors);

            default:
                throw new InvalidOperationException("Resultado de validação desconhecido.");
        }

        if (notification.ApplicationId != expectedApplicationId)
        {
            metrics.Record(notification.Topic, NotificationResult.IgnoredApp);
            return new ReceiveNotificationResult.Received();
        }

        if (!string.Equals(notification.Topic, ItemsTopic, StringComparison.Ordinal))
        {
            metrics.Record(notification.Topic, NotificationResult.IgnoredTopic);
            return new ReceiveNotificationResult.Received();
        }

        var accepted = await inbox.TryAcceptAsync(notification, clock.GetUtcNow(), cancellationToken);
        metrics.Record(notification.Topic, accepted ? NotificationResult.Accepted : NotificationResult.Duplicate);
        return new ReceiveNotificationResult.Received();
    }
}

/// <summary>Resultado de <see cref="ReceiveNotificationHandler"/>.</summary>
public abstract record ReceiveNotificationResult
{
    private ReceiveNotificationResult()
    {
    }

    /// <summary>200 vazio: aceita, duplicada ou ignorada.</summary>
    public sealed record Received : ReceiveNotificationResult;

    /// <summary>400 <c>VALIDATION_FAILED</c>, com os erros por campo JSON.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ReceiveNotificationResult;

    /// <summary>503 <c>ML_NOT_CONFIGURED</c>: sem <c>ClientId</c> não há como conferir o <c>application_id</c>.</summary>
    public sealed record NotConfigured : ReceiveNotificationResult;
}
