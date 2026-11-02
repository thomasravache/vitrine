using System.Text.RegularExpressions;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Atributo pedido no <c>PUT /items/{id}</c>: <c>{ id, value_id? | value_name? }</c>.</summary>
internal sealed record AttributeUpdate(string Id, string? ValueId, string? ValueName);

/// <summary>Corpo do <c>PUT /items/{id}</c>: <c>{ title?, attributes? }</c>; campo nulo não é alterado.</summary>
internal sealed record ItemUpdate(string? Title, IReadOnlyList<AttributeUpdate>? Attributes);

/// <summary>
/// Resultado da edição: <see cref="Item"/> é o anúncio atualizado quando <see cref="Error"/> é nulo, ou o anúncio
/// original sem mudança nenhuma quando a edição foi recusada (atômica).
/// </summary>
internal sealed record ItemEditResult(ItemInfo Item, ApiError? Error);

/// <summary>
/// Regras de edição do ML imitadas pelo simulador (SPEC-0034, seção 6): puras, sem I/O e independentes da
/// <c>ListingChangeRules</c> do Vitrine (o simulador é um oráculo à parte).
/// </summary>
internal static partial class ItemEditRules
{
    /// <summary><c>cause.code</c> do título bloqueado (vendas ou catálogo), como o ML.</summary>
    public const string FieldNotUpdatable = "field_not_updatable";

    /// <summary><c>cause.code</c> de título vazio ou acima do limite (sintético).</summary>
    public const string TitleLengthInvalid = "item.title.length.invalid";

    /// <summary><c>cause.code</c> de atributo desconhecido, não editável ou com valor/unidade fora da lista (sintético).</summary>
    public const string AttributesInvalid = "item.attributes.invalid";

    /// <summary><c>cause.code</c> de anúncio fora de <c>active</c> (sintético; Emenda v1.1).</summary>
    public const string StatusNotActive = "item.status.not_active";

    /// <summary><c>cause.code</c> de descrição com HTML, entidade ou emoji, como o ML.</summary>
    public const string DescriptionTypeInvalid = "item.description.type.invalid";

    /// <summary><c>cause.code</c> de descrição acima do limite da categoria (sintético).</summary>
    public const string DescriptionMaxLength = "item.description.max_length";

    private const string ValidationMessage = "Validation error";
    private const string ValidationError = "validation_error";

    /// <summary>Aplica o <c>PUT /items/{id}</c> ao anúncio, ou devolve o erro do ML sem mudar nada.</summary>
    public static ItemEditResult Apply(ItemInfo item, SimulatorCategory category, ItemUpdate update, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(update);

        if (item.Status != "active")
        {
            return new ItemEditResult(
                item,
                new ApiError(
                    "Item is not active",
                    ValidationError,
                    StatusCodes.Status400BadRequest,
                    [new ApiCause(StatusNotActive, $"Item {item.Id} cannot be modified with status {item.Status}.")]));
        }

        var causes = new List<ApiCause>();
        if (update.Title is { } title)
        {
            if (item.SoldQuantity > 0 || item.CatalogListing)
            {
                causes.Add(new ApiCause(FieldNotUpdatable, "title is not modifiable."));
            }
            else if (string.IsNullOrWhiteSpace(title) || title.Length > category.MaxTitleLength)
            {
                causes.Add(new ApiCause(
                    TitleLengthInvalid,
                    $"title must have between 1 and {category.MaxTitleLength} characters."));
            }
        }

        var attributes = item.Attributes.ToList();
        foreach (var change in update.Attributes ?? [])
        {
            if (Resolve(category, change, out var cause) is { } attribute)
            {
                var index = attributes.FindIndex(existing => existing.Id == attribute.Id);
                if (index >= 0)
                {
                    attributes[index] = attribute;
                }
                else
                {
                    attributes.Add(attribute);
                }
            }
            else
            {
                causes.Add(cause!);
            }
        }

        if (causes.Count > 0)
        {
            return new ItemEditResult(item, new ApiError(ValidationMessage, ValidationError, StatusCodes.Status400BadRequest, causes));
        }

        return new ItemEditResult(
            item with { Title = update.Title ?? item.Title, LastUpdated = now, Attributes = attributes },
            null);
    }

    /// <summary>Erro do ML para a descrição em texto puro; <c>null</c> quando é aceita.</summary>
    public static ApiError? ValidateDescription(SimulatorCategory category, string plainText)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(plainText);

        if (HtmlTag().IsMatch(plainText) || HtmlEntity().IsMatch(plainText) || HasEmoji(plainText))
        {
            return DescriptionError(DescriptionTypeInvalid, "Description plain_text must not contain HTML tags, entities or emojis.");
        }

        return plainText.Length > category.MaxDescriptionLength
            ? DescriptionError(DescriptionMaxLength, $"Description plain_text must have at most {category.MaxDescriptionLength} characters.")
            : null;
    }

    /// <summary>Atributo como fica no anúncio; <c>null</c> (com a <paramref name="cause"/>) quando o ML recusa.</summary>
    private static ItemAttribute? Resolve(SimulatorCategory category, AttributeUpdate change, out ApiCause? cause)
    {
        var definition = category.Attributes.FirstOrDefault(attribute => attribute.Id == change.Id);
        string? problem = null;
        ItemAttribute? resolved = null;
        if (definition is null)
        {
            problem = $"Attribute {change.Id} is not valid for category {category.Id}.";
        }
        else if (definition.ReadOnly)
        {
            problem = $"Attribute {change.Id} is read only.";
        }
        else if (definition.Hidden)
        {
            problem = $"Attribute {change.Id} is hidden and cannot be modified.";
        }
        else if (definition.Values.Count > 0)
        {
            // Lista (ou booleano): value_id da lista; value_name só quando é o nome de um valor da lista.
            var value = change.ValueId is not null
                ? definition.Values.FirstOrDefault(v => v.Id == change.ValueId)
                : definition.Values.FirstOrDefault(v => string.Equals(v.Name, change.ValueName?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                problem = $"Value {change.ValueId ?? change.ValueName} is not valid for attribute {change.Id}.";
            }
            else
            {
                resolved = new ItemAttribute(definition.Id, definition.Name, value.Id, value.Name);
            }
        }
        else if (definition.ValueType == "number_unit")
        {
            // number_unit: "{número} {unidade}" em value_name, com a unidade em allowed_units.
            var match = change.ValueId is null && change.ValueName is not null ? NumberWithUnit().Match(change.ValueName) : null;
            if (match is not { Success: true }
                || !definition.AllowedUnits.Any(unit => string.Equals(unit.Name, match.Groups["unit"].Value, StringComparison.Ordinal)))
            {
                problem = $"Value {change.ValueId ?? change.ValueName} has a unit not allowed for attribute {change.Id}.";
            }
            else
            {
                resolved = new ItemAttribute(definition.Id, definition.Name, null, change.ValueName!.Trim());
            }
        }
        else if (change.ValueId is not null || string.IsNullOrWhiteSpace(change.ValueName))
        {
            problem = $"Attribute {change.Id} needs a value_name.";
        }
        else
        {
            resolved = new ItemAttribute(definition.Id, definition.Name, null, change.ValueName);
        }

        cause = problem is null ? null : new ApiCause(AttributesInvalid, problem);
        return resolved;
    }

    private static ApiError DescriptionError(string code, string message) =>
        new(ValidationMessage, ValidationError, StatusCodes.Status400BadRequest, [new ApiCause(code, message)]);

    /// <summary>Emoji: símbolos fora do plano básico, símbolos diversos/dingbats e seletores/junções de emoji.</summary>
    private static bool HasEmoji(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value >= 0x1F000 || rune.Value is (>= 0x2600 and <= 0x27BF) or 0xFE0F or 0x200D)
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z!][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"&(#[0-9]+|#[xX][0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);")]
    private static partial Regex HtmlEntity();

    [GeneratedRegex(@"^\s*(?<number>[0-9]+(?:[.,][0-9]+)?)\s*(?<unit>\S+)\s*$")]
    private static partial Regex NumberWithUnit();
}
