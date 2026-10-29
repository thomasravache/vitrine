using System.Collections;
using System.Text.RegularExpressions;
using Vitrine.ServiceDefaults.Logging;

namespace Vitrine.Api.Observability;

/// <summary>
/// Mascaramento de valores arbitrários anexados a um evento do Sentry (<c>Mechanism.Data</c>, <c>contexts</c>,
/// <c>extra</c>), que o SDK serializa como JSON (SPEC-0018, ADR-0006): chave de nome sensível vira <c>***</c>,
/// dicionários e coleções são percorridos, textos passam pelo <see cref="SensitiveDataMasker"/> e os demais objetos
/// viram o próprio texto mascarado (ou <c>***</c> quando não dá para obtê-lo com segurança). Chaves são mantidas.
/// </summary>
internal static partial class SentryDataMasker
{
    // Profundidade máxima percorrida; abaixo dela o valor inteiro vira ***.
    private const int MaxDepth = 8;

    /// <summary>
    /// Junta a <paramref name="sensitiveValues"/> os valores (texto) das chaves de nome sensível em
    /// <paramref name="value"/>, inclusive em dicionários e pares aninhados, para mascará-los onde se repetirem
    /// (mensagem da exceção, mensagem do evento, breadcrumbs).
    /// </summary>
    public static void CollectSensitiveValues(string? key, object? value, ICollection<string> sensitiveValues) =>
        Collect(key, value, sensitiveValues, SensitiveDataMasker.IsSensitiveName(key), 0);

    /// <summary>Junta os valores sensíveis de <see cref="Exception.Data"/> da exceção e das internas.</summary>
    public static void CollectSensitiveValues(Exception? exception, ICollection<string> sensitiveValues)
    {
        var pending = new Stack<(Exception Exception, int Depth)>();
        if (exception is not null)
        {
            pending.Push((exception, 0));
        }

        while (pending.TryPop(out var current))
        {
            CollectSensitiveValues(null, current.Exception.Data, sensitiveValues);
            if (current.Depth >= MaxDepth)
            {
                continue;
            }

            if (current.Exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push((inner, current.Depth + 1));
                }
            }
            else if (current.Exception.InnerException is { } inner)
            {
                pending.Push((inner, current.Depth + 1));
            }
        }
    }

    /// <summary>Valor mascarado para a chave <paramref name="key"/>; ver <see cref="SentryDataMasker"/>.</summary>
    public static object? MaskValue(string? key, object? value, IReadOnlyCollection<string> sensitiveValues) =>
        Mask(key, value, sensitiveValues, 0);

    private static object? Mask(string? key, object? value, IReadOnlyCollection<string> sensitiveValues, int depth)
    {
        if (SensitiveDataMasker.IsSensitiveName(key))
        {
            return SensitiveDataMasker.Mask;
        }

        switch (value)
        {
            case null:
                return null;
            case string text:
                return SensitiveDataMasker.MaskText(text, sensitiveValues);
            case var scalar when IsScalar(scalar):
                return scalar;
            case var _ when depth >= MaxDepth:
                return SensitiveDataMasker.Mask;
            case IDictionary dictionary:
                {
                    var masked = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (entryKey, entryValue) in Entries(dictionary))
                    {
                        masked[SensitiveDataMasker.MaskText(entryKey, sensitiveValues)] = Mask(entryKey, entryValue, sensitiveValues, depth + 1);
                    }

                    return masked;
                }

            case var pair when TryGetPair(pair, out var pairKey, out var pairValue):
                {
                    var maskedValue = Mask(pairKey, pairValue, sensitiveValues, depth + 1);
                    return $"[{SensitiveDataMasker.MaskText(pairKey, sensitiveValues)}, {AsText(maskedValue)}]";
                }

            case IEnumerable items:
                return items.Cast<object?>().Select(item => Mask(null, item, sensitiveValues, depth + 1)).ToList();
            default:
                return MaskObjectText(value, sensitiveValues);
        }
    }

    // Objeto qualquer (tipo anônimo, record, struct): o próprio texto mascarado. O ToString de tipo anônimo/record
    // usa "Nome = valor", com espaços que o padrão nome=valor do SensitiveDataMasker não aceita.
    private static string MaskObjectText(object value, IReadOnlyCollection<string> sensitiveValues)
    {
        string? text;
        try
        {
            text = SensitiveDataMasker.Text(value);
        }
#pragma warning disable CA1031 // ToString de terceiro pode lançar qualquer exceção; o valor não é representável.
        catch (Exception)
#pragma warning restore CA1031
        {
            return SensitiveDataMasker.Mask;
        }

        if (text is null)
        {
            return SensitiveDataMasker.Mask;
        }

        var masked = SensitiveDataMasker.MaskText(text, sensitiveValues);
        return SpacedPairPattern().Replace(masked, pair => SensitiveDataMasker.IsSensitiveName(pair.Groups["name"].Value)
            ? $"{pair.Groups["name"].Value} = {SensitiveDataMasker.Mask}"
            : pair.Value);
    }

    private static void Collect(string? key, object? value, ICollection<string> sensitiveValues, bool sensitive, int depth)
    {
        switch (value)
        {
            case null:
                return;
            case string or IFormattable when sensitive:
                {
                    if (SensitiveDataMasker.Text(value) is { Length: > 0 } text && text != SensitiveDataMasker.Mask)
                    {
                        sensitiveValues.Add(text);
                    }

                    return;
                }

            case string:
            case var scalar when IsScalar(scalar):
                return;
            case var _ when depth >= MaxDepth:
                return;
            case IDictionary dictionary:
                foreach (var (entryKey, entryValue) in Entries(dictionary))
                {
                    Collect(entryKey, entryValue, sensitiveValues, sensitive || SensitiveDataMasker.IsSensitiveName(entryKey), depth + 1);
                }

                return;
            case var pair when TryGetPair(pair, out var pairKey, out var pairValue):
                Collect(pairKey, pairValue, sensitiveValues, sensitive || SensitiveDataMasker.IsSensitiveName(pairKey), depth + 1);
                return;
            case IEnumerable items:
                foreach (var item in items)
                {
                    Collect(key, item, sensitiveValues, sensitive, depth + 1);
                }

                return;
            default:
                return;
        }
    }

    private static IEnumerable<(string Key, object? Value)> Entries(IDictionary dictionary)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            yield return (SensitiveDataMasker.Text(entry.Key) ?? string.Empty, entry.Value);
        }
    }

    // KeyValuePair<,> (struct serializado pelo SDK como {Key, Value}): a chave decide se o valor é sensível.
    private static bool TryGetPair(object value, out string key, out object? pairValue)
    {
        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            key = SensitiveDataMasker.Text(type.GetProperty(nameof(KeyValuePair<,>.Key))!.GetValue(value)) ?? string.Empty;
            pairValue = type.GetProperty(nameof(KeyValuePair<,>.Value))!.GetValue(value);
            return true;
        }

        key = string.Empty;
        pairValue = null;
        return false;
    }

    // Números, datas, Guid e enums não carregam e-mail nem credencial e o SDK os serializa como estão.
    private static bool IsScalar(object value) =>
        value is Enum or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or DateOnly or TimeOnly
        || value.GetType().IsPrimitive;

    private static string AsText(object? masked) => masked switch
    {
        null => string.Empty,
        string text => text,
        var scalar when IsScalar(scalar) => SensitiveDataMasker.Text(scalar) ?? string.Empty,
        _ => SensitiveDataMasker.Mask,
    };

    // "Nome = valor" (ToString de tipo anônimo/record); o valor vai até a próxima ", Nome =", chave/colchete ou fim.
    [GeneratedRegex(
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>(?:(?!,\s*[A-Za-z_][A-Za-z0-9_]*\s*=)[^{}\[\]\r\n])*?)(?=\s*(?:,\s*[A-Za-z_][A-Za-z0-9_]*\s*=|[{}\[\]\r\n]|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpacedPairPattern();
}
