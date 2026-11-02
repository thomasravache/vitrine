using System.Globalization;
using System.Text.Json;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Traduz a resposta de erro de uma escrita no ML (<c>{ message, error, status, cause: [{ code, message }] }</c>) para os
/// códigos estáveis do contrato da SPEC-0033 (seção 6). Os códigos do ML não foram verificados (fixtures sintéticas): a
/// tabela é conferida com usuário de teste real no go-live (SPEC-0009).
/// </summary>
internal static class MercadoLivreErrorMapper
{
    public const string FieldNotUpdatable = "FIELD_NOT_UPDATABLE";
    public const string DescriptionInvalid = "DESCRIPTION_INVALID";
    public const string ItemNotActive = "ITEM_NOT_ACTIVE";
    public const string Conflict = "ML_CONFLICT";
    public const string Forbidden = "ML_FORBIDDEN";
    public const string Rejected = "ML_REJECTED";
    public const string Unavailable = "ML_UNAVAILABLE";

    /// <summary>Teto da mensagem do ML repassada ao chamador.</summary>
    public const int MaxMessageLength = 300;

    private const string GenericRejection = "O Mercado Livre recusou a alteração.";

    /// <summary>Resultado <see cref="FieldResult.Rejected"/> para o <paramref name="statusCode"/> e o corpo cru do ML.</summary>
    public static FieldResult.Rejected Map(int statusCode, string? body)
    {
        if (statusCode == 429 || statusCode >= 500)
        {
            return UnavailableResult(string.Create(CultureInfo.InvariantCulture, $"o Mercado Livre respondeu {statusCode}"));
        }

        var error = Parse(body);
        switch (statusCode)
        {
            case 403:
                return new(Forbidden, error.Message ?? "O Mercado Livre negou a permissão de escrita (escopo write).");
            case 409:
                return new(Conflict, error.Message ?? "O anúncio foi alterado ao mesmo tempo por outra escrita (optimistic locking).");
        }

        // Título não modificável (emenda v1.1): só quando a cause (código, mensagem ou referência) cita o título. Um
        // field_not_updatable de outro campo é recusa do corpo inteiro (ML_REJECTED, abaixo).
        if (error.Causes.FirstOrDefault(IsTitleNotUpdatable) is { } titleCause)
        {
            return new(FieldNotUpdatable, titleCause.Message ?? error.Message ?? "O título não pode ser alterado.");
        }

        if (Mentions(error.Message, "title") && (Mentions(error.Message, "not modifiable") || Mentions(error.Message, "cannot be updated")))
        {
            return new(FieldNotUpdatable, error.Message!);
        }

        if (error.Causes.Any(cause => IsCode(cause.Code, "item.description.type.invalid")))
        {
            return new(DescriptionInvalid, CauseMessage(error, "item.description.type.invalid") ?? "Descrição inválida.");
        }

        if (error.Causes.Any(cause => IsNotActiveCode(cause.Code)) || Mentions(error.Message, "not active"))
        {
            return new(ItemNotActive, error.Message ?? "O anúncio não está ativo.");
        }

        return new(Rejected, error.Message ?? GenericRejection);
    }

    /// <summary>Falha de rede, timeout, circuito aberto, 429 ou 5xx: o resultado da escrita é incerto ou nulo.</summary>
    public static FieldResult.Rejected UnavailableResult(string reason) => new(Unavailable, $"Mercado Livre indisponível: {reason}.");

    private static bool IsTitleNotUpdatable(MlCause cause) =>
        (IsCode(cause.Code, "field_not_updatable") || Mentions(cause.Message, "not modifiable"))
        && (Mentions(cause.Message, "title") || cause.References.Any(reference => Mentions(reference, "title")));

    private static bool IsCode(string? code, string expected) => string.Equals(code, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsNotActiveCode(string? code) =>
        code is not null
        && code.Contains("status", StringComparison.OrdinalIgnoreCase)
        && code.Contains("not_active", StringComparison.OrdinalIgnoreCase);

    private static bool Mentions(string? text, string fragment) =>
        text is not null && text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string? CauseMessage(MlError error, string code) =>
        error.Causes.FirstOrDefault(cause => IsCode(cause.Code, code) && cause.Message is not null)?.Message ?? error.Message;

    private static MlError Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return MlError.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return MlError.Empty;
            }

            var causes = new List<MlCause>();
            if (root.TryGetProperty("cause", out var cause) && cause.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in cause.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
                {
                    causes.Add(new MlCause(Text(entry, "code"), Text(entry, "message"), References(entry)));
                }
            }

            return new MlError(Text(root, "message"), causes);
        }
        catch (JsonException)
        {
            return MlError.Empty;
        }
    }

    /// <summary>Texto de uma propriedade string, aparado e limitado a <see cref="MaxMessageLength"/>; nulo se vazio.</summary>
    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    private static List<string> References(JsonElement cause) =>
        cause.TryGetProperty("references", out var references) && references.ValueKind == JsonValueKind.Array
            ? [.. references.EnumerateArray().Where(reference => reference.ValueKind == JsonValueKind.String).Select(reference => reference.GetString()!)]
            : [];

    private sealed record MlCause(string? Code, string? Message, IReadOnlyList<string> References);

    private sealed record MlError(string? Message, IReadOnlyList<MlCause> Causes)
    {
        public static readonly MlError Empty = new(null, []);
    }
}
