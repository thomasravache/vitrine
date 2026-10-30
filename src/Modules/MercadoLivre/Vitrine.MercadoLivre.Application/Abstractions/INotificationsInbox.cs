using Vitrine.MercadoLivre.Application.Notifications;

namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>
/// Inbox das notificações do ML (SPEC-0023, ADR-0014): grava a notificação e enfileira o processamento durável na mesma
/// transação (outbox), deduplicando pelo <c>_id</c>.
/// </summary>
public interface INotificationsInbox
{
    /// <summary>
    /// Grava a notificação aceita em <paramref name="acceptedAt"/> e enfileira o processamento; <c>false</c> quando o
    /// <c>_id</c> já estava gravado (nada novo é gravado nem enfileirado).
    /// </summary>
    Task<bool> TryAcceptAsync(MercadoLivreNotification notification, DateTimeOffset acceptedAt, CancellationToken cancellationToken);
}
