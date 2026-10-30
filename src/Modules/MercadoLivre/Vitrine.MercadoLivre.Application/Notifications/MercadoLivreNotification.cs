namespace Vitrine.MercadoLivre.Application.Notifications;

/// <summary>
/// Notificação do ML já validada (contrato da SPEC-0023, §6): só os campos que o inbox guarda. <c>actions</c> e qualquer
/// outro campo do corpo são descartados.
/// </summary>
/// <param name="NotificationId"><c>_id</c> da notificação (não vazio).</param>
/// <param name="Resource"><c>resource</c> (ex.: <c>/items/MLB1234567890</c>); o formato é conferido no processamento.</param>
/// <param name="MlUserId"><c>user_id</c> do vendedor (int64).</param>
/// <param name="Topic"><c>topic</c> (ex.: <c>items</c>).</param>
/// <param name="ApplicationId"><c>application_id</c> do app no DevCenter (int64).</param>
/// <param name="Attempts"><c>attempts</c>.</param>
/// <param name="Sent"><c>sent</c> (UTC).</param>
/// <param name="Received"><c>received</c> (UTC).</param>
public sealed record MercadoLivreNotification(
    string NotificationId,
    string Resource,
    long MlUserId,
    string Topic,
    long ApplicationId,
    int Attempts,
    DateTimeOffset Sent,
    DateTimeOffset Received);
