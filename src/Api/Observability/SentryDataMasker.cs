using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vitrine.ServiceDefaults.Logging;

namespace Vitrine.Api.Observability;

/// <summary>
/// Mascaramento de valores arbitrários anexados a um evento do Sentry (<c>Mechanism.Data</c>, <c>contexts</c>,
/// <c>extra</c>), que o SDK serializa como JSON (SPEC-0018, ADR-0006). Chave de nome sensível vira <c>***</c> (com
/// tudo o que estiver abaixo dela); dicionários, coleções já materializadas, <see cref="JsonElement"/> e objetos
/// (lidos pelas propriedades e campos públicos) são percorridos; textos passam pelo <see cref="SensitiveDataMasker"/>.
/// O <c>ToString</c> só é usado quando o objeto não tem membros públicos ou é de tipo da plataforma. Nada aqui lança:
/// o que não dá para ler com segurança (enumeração preguiçosa, getter ou <c>ToString</c> que lança, ciclo,
/// profundidade excessiva) vira <c>***</c>. Chaves são mantidas.
/// </summary>
internal static partial class SentryDataMasker
{
    // Profundidade máxima percorrida; abaixo dela o valor inteiro vira ***.
    private const int MaxDepth = 8;

    // Limite de itens/membros lidos por nó; o excedente é descartado (evita grafos enormes no evento).
    private const int MaxEntries = 100;

    // Textos até 3 caracteres não entram na lista de valores sensíveis: trocar "1" ou "abc" em toda a mensagem
    // corromperia o texto (continuam virando *** sob a própria chave).
    private const int MinCollectedLength = 4;

    // Marca de membro cujo getter lançou: vira ***.
    private static readonly object Unreadable = new();

    /// <summary>
    /// Junta a <paramref name="sensitiveValues"/> os textos sob chaves de nome sensível em <paramref name="value"/>
    /// (inclusive em dicionários, objetos e pares aninhados), para mascará-los onde se repetirem (mensagem da exceção,
    /// mensagem do evento, breadcrumbs). Números, valores escalares e textos curtos não entram. Nunca lança.
    /// </summary>
    public static void CollectSensitiveValues(string? key, object? value, ICollection<string> sensitiveValues)
    {
        try
        {
            Collect(value, sensitiveValues, SensitiveDataMasker.IsSensitiveName(key), 0, NewVisited());
        }
#pragma warning disable CA1031 // Mascaramento nunca pode derrubar o registro do erro.
        catch (Exception)
#pragma warning restore CA1031
        {
            // O que não deu para ler segue mascarado por chave em MaskValue.
        }
    }

    /// <summary>Junta os valores sensíveis de <see cref="Exception.Data"/> da exceção e das internas. Nunca lança.</summary>
    public static void CollectSensitiveValues(Exception? exception, ICollection<string> sensitiveValues)
    {
        try
        {
            var pending = new Stack<(Exception Exception, int Depth)>();
            var visited = NewVisited();
            if (exception is not null)
            {
                pending.Push((exception, 0));
            }

            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current.Exception))
                {
                    continue;
                }

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
#pragma warning disable CA1031 // Mascaramento nunca pode derrubar o registro do erro.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Exceção de terceiro com Data/InnerException que lança: segue sem os valores dela.
        }
    }

    /// <summary>Valor mascarado para a chave <paramref name="key"/>; ver <see cref="SentryDataMasker"/>. Nunca lança.</summary>
    public static object? MaskValue(string? key, object? value, IReadOnlyCollection<string> sensitiveValues)
    {
        try
        {
            return Mask(key, value, sensitiveValues, 0, NewVisited());
        }
#pragma warning disable CA1031 // Mascaramento nunca pode derrubar o envio do evento.
        catch (Exception)
#pragma warning restore CA1031
        {
            return SensitiveDataMasker.Mask;
        }
    }

    private static HashSet<object> NewVisited() => new(ReferenceEqualityComparer.Instance);

    private static object? Mask(string? key, object? value, IReadOnlyCollection<string> sensitiveValues, int depth, HashSet<object> visited)
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
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return SensitiveDataMasker.MaskText(element.GetString() ?? string.Empty, sensitiveValues);
            case JsonElement { ValueKind: not (JsonValueKind.Object or JsonValueKind.Array) } element:
                return element;
            case var scalar when IsScalar(scalar):
                return scalar;
            case var _ when depth >= MaxDepth || ReferenceEquals(value, Unreadable):
                return SensitiveDataMasker.Mask;
            default:
                break;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            // Ciclo: o objeto já está no caminho atual.
            return SensitiveDataMasker.Mask;
        }

        try
        {
            if (TryGetPair(value, out var pairKey, out var pairValue))
            {
                var maskedValue = Mask(pairKey, pairValue, sensitiveValues, depth + 1, visited);
                return $"[{SensitiveDataMasker.MaskText(pairKey, sensitiveValues)}, {AsText(maskedValue)}]";
            }

            switch (Expand(value))
            {
                case List<(string Key, object? Value)> entries:
                    {
                        var masked = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var (entryKey, entryValue) in entries)
                        {
                            masked[SensitiveDataMasker.MaskText(entryKey, sensitiveValues)] = Mask(entryKey, entryValue, sensitiveValues, depth + 1, visited);
                        }

                        return masked;
                    }

                case List<object?> items:
                    return items.Select(item => Mask(null, item, sensitiveValues, depth + 1, visited)).ToList();
                case string text:
                    return MaskObjectText(text, sensitiveValues);
                default:
                    return SensitiveDataMasker.Mask;
            }
        }
#pragma warning disable CA1031 // Valor que não dá para ler com segurança vira ***.
        catch (Exception)
#pragma warning restore CA1031
        {
            return SensitiveDataMasker.Mask;
        }
        finally
        {
            visited.Remove(value);
        }
    }

    private static void Collect(object? value, ICollection<string> sensitiveValues, bool sensitive, int depth, HashSet<object> visited)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                Add(text, sensitiveValues, sensitive);
                return;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                Add(element.GetString(), sensitiveValues, sensitive);
                return;
            case JsonElement { ValueKind: not (JsonValueKind.Object or JsonValueKind.Array) }:
                return;
            case var scalar when IsScalar(scalar):
                return;
            case var _ when depth >= MaxDepth || ReferenceEquals(value, Unreadable):
                return;
            default:
                break;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            return;
        }

        try
        {
            if (TryGetPair(value, out var pairKey, out var pairValue))
            {
                Collect(pairValue, sensitiveValues, sensitive || SensitiveDataMasker.IsSensitiveName(pairKey), depth + 1, visited);
                return;
            }

            switch (Expand(value))
            {
                case List<(string Key, object? Value)> entries:
                    foreach (var (entryKey, entryValue) in entries)
                    {
                        Collect(entryValue, sensitiveValues, sensitive || SensitiveDataMasker.IsSensitiveName(entryKey), depth + 1, visited);
                    }

                    break;
                case List<object?> items:
                    foreach (var item in items)
                    {
                        Collect(item, sensitiveValues, sensitive, depth + 1, visited);
                    }

                    break;
                default:
                    // Texto de ToString (tipo da plataforma ou sem membros públicos): não é coletado.
                    break;
            }
        }
#pragma warning disable CA1031 // O que não dá para ler segue mascarado por chave em Mask.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Sem os valores deste nó; o mascaramento por chave continua valendo.
        }
        finally
        {
            visited.Remove(value);
        }
    }

    private static void Add(string? text, ICollection<string> sensitiveValues, bool sensitive)
    {
        if (sensitive && text is { Length: >= MinCollectedLength } && text != SensitiveDataMasker.Mask)
        {
            sensitiveValues.Add(text);
        }
    }

    /// <summary>
    /// Lê a estrutura de <paramref name="value"/>, já materializada: entradas (dicionário, <see cref="JsonElement"/>
    /// objeto, coleção de pares, objeto lido por reflexão), itens (array, coleção, <see cref="JsonElement"/> array), o
    /// texto do <c>ToString</c> (tipo da plataforma ou sem membros públicos) ou <see langword="null"/> quando não é
    /// seguro ler (enumeração preguiçosa, <c>ToString</c> que lança).
    /// </summary>
    private static object? Expand(object value)
    {
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return element.EnumerateObject().Take(MaxEntries).Select(property => (property.Name, (object?)property.Value)).ToList();
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                return element.EnumerateArray().Take(MaxEntries).Select(item => (object?)item).ToList();
            case IDictionary dictionary:
                {
                    var entries = new List<(string Key, object? Value)>();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entries.Count >= MaxEntries)
                        {
                            break;
                        }

                        entries.Add((SafeText(entry.Key) ?? string.Empty, entry.Value));
                    }

                    return entries;
                }

            case IEnumerable items when IsMaterialized(items):
                return ReadItems(items);
            case IEnumerable:
                // Enumeração preguiçosa (iterador, IQueryable): enumerar poderia lançar ou ter efeito colateral.
                return null;
            default:
                break;
        }

        var type = value.GetType();
        if (!IsPlatformType(type) && ReadMembers(value, type) is { Count: > 0 } members)
        {
            return members;
        }

        return SafeText(value);
    }

    // Itens de uma coleção materializada; coleção de pares (IDictionary<,> sem IDictionary) vira entradas.
    private static object ReadItems(IEnumerable items)
    {
        var list = new List<object?>();
        foreach (var item in items)
        {
            if (list.Count >= MaxEntries)
            {
                break;
            }

            list.Add(item);
        }

        var pairs = new List<(string Key, object? Value)>();
        foreach (var item in list)
        {
            if (item is null || !TryGetPair(item, out var pairKey, out var pairValue))
            {
                return list;
            }

            pairs.Add((pairKey, pairValue));
        }

        return list.Count > 0 ? pairs : list;
    }

    // Propriedades e campos públicos de instância (record, tipo anônimo, struct, classe do domínio).
    private static List<(string Key, object? Value)> ReadMembers(object value, Type type)
    {
        var members = new List<(string Key, object? Value)>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (members.Count >= MaxEntries)
            {
                return members;
            }

            if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true })
            {
                continue;
            }

            members.Add((property.Name, Read(() => property.GetValue(value))));
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (members.Count >= MaxEntries)
            {
                return members;
            }

            members.Add((field.Name, Read(() => field.GetValue(value))));
        }

        return members;
    }

    private static object? Read(Func<object?> getter)
    {
        try
        {
            return getter();
        }
#pragma warning disable CA1031 // Getter de terceiro pode lançar qualquer exceção; o membro vira ***.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Unreadable;
        }
    }

    private static string? SafeText(object? value)
    {
        try
        {
            return SensitiveDataMasker.Text(value);
        }
#pragma warning disable CA1031 // ToString/IFormattable de terceiro pode lançar qualquer exceção.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    // Só coleções já materializadas são enumeradas (arrays, ICollection, ICollection<>/IReadOnlyCollection<>).
    private static bool IsMaterialized(IEnumerable items) =>
        items is Array or ICollection
        || items.GetType().GetInterfaces().Any(contract => contract.IsGenericType
            && (contract.GetGenericTypeDefinition() == typeof(ICollection<>)
                || contract.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>)));

    // Tipos da plataforma e do SDK (Uri, Type, Version, tipos do Sentry...): ToString em vez de reflexão.
    private static bool IsPlatformType(Type type) =>
        type.Namespace is { } name
        && (name == "System" || name.StartsWith("System.", StringComparison.Ordinal)
            || name == "Microsoft" || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name == "Sentry" || name.StartsWith("Sentry.", StringComparison.Ordinal));

    // Texto de ToString (fallback): mascarado, inclusive o formato "Nome = valor" de tipo anônimo/record.
    private static string MaskObjectText(string text, IReadOnlyCollection<string> sensitiveValues)
    {
        var masked = SensitiveDataMasker.MaskText(text, sensitiveValues);
        return SpacedPairPattern().Replace(masked, pair => SensitiveDataMasker.IsSensitiveName(pair.Groups["name"].Value)
            ? $"{pair.Groups["name"].Value} = {SensitiveDataMasker.Mask}"
            : pair.Value);
    }

    // KeyValuePair<,>: a chave decide se o valor é sensível.
    private static bool TryGetPair(object value, out string key, out object? pairValue)
    {
        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            key = SafeText(type.GetProperty("Key")!.GetValue(value)) ?? string.Empty;
            pairValue = type.GetProperty("Value")!.GetValue(value);
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
        var scalar when IsScalar(scalar) => SafeText(scalar) ?? SensitiveDataMasker.Mask,
        _ => SensitiveDataMasker.Mask,
    };

    // "Nome = valor" (ToString de tipo anônimo/record); o valor vai até a próxima ", Nome =", chave/colchete ou fim.
    [GeneratedRegex(
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>(?:(?!,\s*[A-Za-z_][A-Za-z0-9_]*\s*=)[^{}\[\]\r\n])*?)(?=\s*(?:,\s*[A-Za-z_][A-Za-z0-9_]*\s*=|[{}\[\]\r\n]|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpacedPairPattern();
}
