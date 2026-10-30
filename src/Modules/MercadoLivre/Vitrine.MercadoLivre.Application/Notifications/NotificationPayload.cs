using System.Text.Json;

namespace Vitrine.MercadoLivre.Application.Notifications;

/// <summary>
/// Validação do corpo de <c>POST /api/v1/mercadolivre/notifications</c> (SPEC-0023, §6): JSON objeto com <c>_id</c>
/// (string não vazia, até <see cref="MaxNotificationIdLength"/> caracteres), <c>resource</c> e <c>topic</c> (strings),
/// <c>user_id</c> e <c>application_id</c> (números int64), <c>attempts</c> (número int32), <c>sent</c> e
/// <c>received</c> (datas ISO 8601). <c>actions</c> é opcional; campos desconhecidos são ignorados. Os tipos são
/// estritos (número em texto é recusado) e as mensagens de erro nunca repetem o valor recebido.
/// </summary>
public static class NotificationPayload
{
    /// <summary>Tamanho máximo do <c>_id</c> (coluna <c>notification_id varchar(64)</c> — emenda v1.1).</summary>
    public const int MaxNotificationIdLength = 64;

    private const string Required = "Campo obrigatório.";

    // Propriedade repetida é recusada: o valor usado não pode depender de qual cópia o leitor escolhe.
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    /// <summary>Valida o corpo UTF-8; inválido devolve os erros por nome do campo JSON (ou <c>body</c>).</summary>
    public static NotificationPayloadResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
        }
        catch (JsonException)
        {
            return InvalidBody();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidBody();
            }

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var notificationId = NotificationId(root, errors);
            var resource = String(root, "resource", errors);
            var mlUserId = Int64(root, "user_id", errors);
            var topic = String(root, "topic", errors);
            var applicationId = Int64(root, "application_id", errors);
            var attempts = Int32(root, "attempts", errors);
            var sent = Timestamp(root, "sent", errors);
            var received = Timestamp(root, "received", errors);

            if (errors.Count > 0)
            {
                return new NotificationPayloadResult.Invalid(errors);
            }

            return new NotificationPayloadResult.Valid(new MercadoLivreNotification(
                notificationId!, resource!, mlUserId, topic!, applicationId, attempts, sent, received));
        }
    }

    private static NotificationPayloadResult.Invalid InvalidBody() =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["body"] = ["O corpo deve ser um objeto JSON válido."] });

    private static string? NotificationId(JsonElement root, Dictionary<string, string[]> errors)
    {
        const string Field = "_id";
        var value = String(root, Field, errors);
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors[Field] = ["Não pode ser vazio."];
            return null;
        }

        if (value.Length > MaxNotificationIdLength)
        {
            errors[Field] = [$"Deve ter no máximo {MaxNotificationIdLength} caracteres."];
            return null;
        }

        return value;
    }

    private static string? String(JsonElement root, string field, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            errors[field] = [Required];
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors[field] = ["Deve ser texto."];
            return null;
        }

        return element.GetString();
    }

    private static long Int64(JsonElement root, string field, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            errors[field] = [Required];
            return 0;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            errors[field] = ["Deve ser um número inteiro de 64 bits."];
            return 0;
        }

        return value;
    }

    private static int Int32(JsonElement root, string field, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            errors[field] = [Required];
            return 0;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            errors[field] = ["Deve ser um número inteiro."];
            return 0;
        }

        return value;
    }

    private static DateTimeOffset Timestamp(JsonElement root, string field, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            errors[field] = [Required];
            return default;
        }

        if (element.ValueKind != JsonValueKind.String || !element.TryGetDateTimeOffset(out var value))
        {
            errors[field] = ["Deve ser uma data ISO 8601."];
            return default;
        }

        return value.ToUniversalTime();
    }
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
