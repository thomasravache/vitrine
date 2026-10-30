namespace Vitrine.MercadoLivre.Application.Notifications;

/// <summary>
/// Validação do corpo de <c>POST /api/v1/mercadolivre/notifications</c> (SPEC-0023, §6): JSON objeto com <c>_id</c>
/// (string não vazia), <c>resource</c> e <c>topic</c> (strings), <c>user_id</c> e <c>application_id</c> (números int64),
/// <c>attempts</c> (número int32), <c>sent</c> e <c>received</c> (datas ISO 8601). <c>actions</c> é opcional; campos
/// desconhecidos são ignorados.
/// </summary>
public static class NotificationPayload
{
    /// <summary>Valida o corpo UTF-8; inválido devolve os erros por nome do campo JSON (ou <c>body</c>).</summary>
    public static NotificationPayloadResult Parse(ReadOnlySpan<byte> utf8Json) =>
        throw new NotImplementedException("SPEC-0023: validação do payload da notificação.");
}

/// <summary>Resultado de <see cref="NotificationPayload.Parse"/>.</summary>
public abstract record NotificationPayloadResult
{
    private NotificationPayloadResult()
    {
    }

    /// <summary>Corpo válido.</summary>
    public sealed record Valid(MercadoLivreNotification Notification) : NotificationPayloadResult;

    /// <summary>Corpo recusado (400 <c>VALIDATION_FAILED</c>): erros por nome do campo JSON (ex.: <c>user_id</c>) ou <c>body</c>.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : NotificationPayloadResult;
}
