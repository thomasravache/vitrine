using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vitrine.MercadoLivre.PublicApi;

/// <summary>
/// Regras locais do Mercado Livre para editar um anúncio (contrato v1 da SPEC-0033): pura, sem I/O, usada pelo editor
/// antes de chamar o ML e pelo módulo Listings para validar a sugestão da IA e os valores finais do vendedor.
/// </summary>
public static partial class ListingChangeRules
{
    /// <summary>Limite da descrição quando a categoria não informa <c>max_description_length</c>.</summary>
    public const int DefaultMaxDescriptionLength = 50000;

    /// <summary>Limite de um valor livre de atributo (não verificado no ML; conferir no go-live).</summary>
    public const int MaxFreeAttributeLength = 255;

    private const string ActiveStatus = "active";
    private const string ValueNotAllowed = "ATTRIBUTE_VALUE_NOT_ALLOWED";
    private const int RegexTimeoutMilliseconds = 1000;

    // Faixa de anos (ex.: 2008-2012) sem dígito depois: não é telefone (autopeças, eletrônicos).
    private const string NotYearRange = @"(?!(?:19|20)[0-9]{2}[-/. ](?:19|20)[0-9]{2}(?![0-9]))";

    /// <summary>
    /// <c>TITLE_LOCKED_SOLD</c> (vendas), <c>TITLE_LOCKED_CATALOG</c> (catálogo), <c>TITLE_LOCKED_USER_PRODUCT</c> (user
    /// product), nessa precedência; nulo quando o título pode ser editado.
    /// </summary>
    public static string? TitleLockReason(MercadoLivreItemDetails item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SoldQuantity > 0)
        {
            return "TITLE_LOCKED_SOLD";
        }

        if (item.CatalogListing)
        {
            return "TITLE_LOCKED_CATALOG";
        }

        return string.IsNullOrEmpty(item.UserProductId) ? null : "TITLE_LOCKED_USER_PRODUCT";
    }

    /// <summary>
    /// Violações das <paramref name="changes"/>; chave <c>item</c> | <c>title</c> | <c>description</c> |
    /// <c>attributes.{id}</c>, valor = código do contrato. Campo nulo em <paramref name="changes"/> não é avaliado.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Violations(
        MercadoLivreItemDetails item, MercadoLivreCategory category, ListingChanges changes)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(changes);

        var violations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.Equals(item.Status, ActiveStatus, StringComparison.Ordinal))
        {
            violations["item"] = "ITEM_NOT_ACTIVE";
        }

        if (changes.Title is not null && TitleViolation(item, category, changes.Title) is { } title)
        {
            violations["title"] = title;
        }

        if (changes.Description is not null && DescriptionViolation(category, changes.Description) is { } description)
        {
            violations["description"] = description;
        }

        foreach (var change in changes.Attributes ?? [])
        {
            if (AttributeViolation(category, change) is { } attribute)
            {
                violations[$"attributes.{change.Id}"] = attribute;
            }
        }

        return violations;
    }

    /// <summary>
    /// <c>ValueName</c> igual a um <c>Values[].Name</c> (sem diferenciar maiúsculas e acentos) vira o <c>ValueId</c>
    /// correspondente.
    /// </summary>
    public static AttributeChange Normalize(MercadoLivreCategoryAttribute spec, AttributeChange change)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(change);
        if (change.ValueId is not null || string.IsNullOrWhiteSpace(change.ValueName) || spec.Values.Count == 0)
        {
            return change;
        }

        var wanted = Fold(change.ValueName);
        var match = spec.Values.FirstOrDefault(value => string.Equals(Fold(value.Name), wanted, StringComparison.Ordinal));
        return match is null ? change : change with { ValueId = match.Id, ValueName = match.Name };
    }

    private static string? TitleViolation(MercadoLivreItemDetails item, MercadoLivreCategory category, string title)
    {
        if (TitleLockReason(item) is { } locked)
        {
            return locked;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return "TITLE_EMPTY";
        }

        return title.Length > category.MaxTitleLength ? "TITLE_TOO_LONG" : null;
    }

    private static string? DescriptionViolation(MercadoLivreCategory category, string description)
    {
        if (HasMarkupOrEmoji(description))
        {
            return "DESCRIPTION_INVALID";
        }

        if (HasContact(description))
        {
            return "DESCRIPTION_CONTACT";
        }

        return description.Length > (category.MaxDescriptionLength ?? DefaultMaxDescriptionLength) ? "DESCRIPTION_TOO_LONG" : null;
    }

    private static string? AttributeViolation(MercadoLivreCategory category, AttributeChange change)
    {
        var spec = category.Attributes.FirstOrDefault(attribute => string.Equals(attribute.Id, change.Id, StringComparison.Ordinal));
        if (spec is null)
        {
            return "ATTRIBUTE_UNKNOWN";
        }

        if (spec.ReadOnly || spec.Hidden)
        {
            return "ATTRIBUTE_NOT_EDITABLE";
        }

        var normalized = Normalize(spec, change);
        if (spec.Values.Count > 0)
        {
            // Lista: o valor precisa ser um dos permitidos (por id, ou por nome já convertido em id).
            return normalized.ValueId is { } valueId && spec.Values.Any(value => string.Equals(value.Id, valueId, StringComparison.Ordinal))
                ? null
                : ValueNotAllowed;
        }

        // Valor livre: só value_name, em texto puro e com até MaxFreeAttributeLength caracteres.
        var valueName = normalized.ValueName;
        if (normalized.ValueId is not null
            || string.IsNullOrWhiteSpace(valueName)
            || valueName.Length > MaxFreeAttributeLength
            || valueName.Any(char.IsControl)
            || HasMarkupOrEmoji(valueName))
        {
            return ValueNotAllowed;
        }

        if (spec.AllowedUnits.Count > 0)
        {
            // number_unit: número seguido de uma das unidades permitidas (ex.: "20 h").
            var match = NumberWithUnit().Match(valueName);
            return match.Success && spec.AllowedUnits.Contains(match.Groups["unit"].Value, StringComparer.Ordinal) ? null : ValueNotAllowed;
        }

        return string.Equals(spec.ValueType, "number", StringComparison.Ordinal) && !Number().IsMatch(valueName) ? ValueNotAllowed : null;
    }

    /// <summary>Tag HTML, entidade HTML (<c>&amp;nbsp;</c>, <c>&amp;#39;</c>) ou emoji (inclusive seletor de variação).</summary>
    private static bool HasMarkupOrEmoji(string text) => HtmlTag().IsMatch(text) || HtmlEntity().IsMatch(text) || HasEmoji(text);

    private static bool HasEmoji(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value >= 0x1F000                       // emoji e pictogramas fora do BMP (inclui tons de pele)
                || rune.Value is >= 0x2600 and <= 0x27BF    // símbolos diversos e dingbats (☀, ❤, ✔)
                || rune.Value is >= 0x2B00 and <= 0x2BFF    // setas e símbolos (⭐)
                || rune.Value is 0xFE0F or 0xFE0E or 0x200D) // seletores de variação e ZWJ
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>E-mail, telefone brasileiro, URL ou "whatsapp" (regra do ML: sem dados de contato).</summary>
    private static bool HasContact(string text) =>
        Email().IsMatch(text) || Url().IsMatch(text) || WhatsApp().IsMatch(text) || Phone().IsMatch(text);

    /// <summary>Maiúsculas e acentos ignorados (<c>Café</c> = <c>cafe</c>).</summary>
    private static string Fold(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().ToUpperInvariant();
    }

    [GeneratedRegex(@"<\s*/?\s*[A-Za-z!][^<>]*>", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"&(?:[A-Za-z][A-Za-z0-9]{1,31}|#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6});", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex HtmlEntity();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex Email();

    // http(s):// e www. em qualquer caixa; domínio solto só com TLD em minúsculas ("novo.Com" não é URL).
    [GeneratedRegex(@"(?i:\bhttps?://|\bwww\.)|\b[A-Za-z0-9-]+\.(?:com|net|org)(?:\.br)?\b", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex Url();

    [GeneratedRegex(@"whats\s?app", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex WhatsApp();

    // Telefone brasileiro (emendas v1.1 e v1.2). Nenhum ramo começa colado a dígito ou letra (inclusive acentuada), nem
    // termina colado a dígito; faixa de anos (2008-2012, 2019/2021) nunca é telefone:
    // - com DDD (com ou sem parênteses) e +55 opcional: 8 ou 9 dígitos, com ou sem separador;
    // - sem DDD: só celular de 9 dígitos começando por 9 (9XXXX-XXXX ou 9XXXXXXXX);
    // - 8 dígitos (XXXX-XXXX, XXXX XXXX, XXXXXXXX) só com palavra de contato inteira até 20 caracteres antes.
    [GeneratedRegex(
        @"(?<![0-9\p{L}])(?:\+?55[ .-]?)?\(?[1-9][0-9]\)?[ .-]?" + NotYearRange + @"9?[0-9]{4}[ .-]?[0-9]{4}(?![0-9])"
        + @"|(?<![0-9\p{L}])9[0-9]{4}-?[0-9]{4}(?![0-9])"
        + @"|(?i:\b(?:telefone|tel|fone|celular|cel|ligue|contato|whats|zap)\b)[^\r\n]{0,20}?(?<![0-9\p{L}])" + NotYearRange
        + @"[0-9]{4}[ .-]?[0-9]{4}(?![0-9])",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex Phone();

    [GeneratedRegex(@"^\s*-?[0-9]+(?:[.,][0-9]+)?\s*(?<unit>\S+)\s*$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex NumberWithUnit();

    [GeneratedRegex(@"^\s*-?[0-9]+(?:[.,][0-9]+)?\s*$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex Number();
}
