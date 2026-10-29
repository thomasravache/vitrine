using System.Globalization;
using System.Text.RegularExpressions;

namespace Vitrine.ServiceDefaults.Logging;

/// <summary>
/// Regras de mascaramento de dados pessoais e segredos em telemetria (ADR-0006): o valor de toda propriedade cujo
/// nome denuncia dado sensível (e-mail, senha, token, <c>Authorization</c>, <c>Cookie</c>...) vira <see cref="Mask"/>;
/// e-mails e credenciais <c>Bearer</c>/<c>Basic</c> são mascarados em qualquer texto, mesmo sob nomes neutros.
/// Usado pelo pipeline de logs do OpenTelemetry, pelo console JSON e pelo Sentry.
/// </summary>
public static partial class SensitiveDataMasker
{
    /// <summary>Valor que substitui o dado sensível.</summary>
    public const string Mask = "***";

    // Trechos de nome (sem caixa, sem separadores) que marcam a propriedade como sensível. Cobre variações como
    // userEmail, newPassword, access_token, refresh-token, Set-Cookie, http.request.header.authorization.
    private static readonly string[] SensitiveNameFragments =
    [
        "email", "password", "passwd", "senha", "secret", "token", "authorization", "cookie", "apikey", "credential",
    ];

    /// <summary>Indica se o nome de uma propriedade denuncia dado sensível.</summary>
    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // O nome inteiro é normalizado (sem truncar): um trecho sensível no fim de um nome longo também conta.
        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        ReadOnlySpan<char> normalized = buffer[..length];
        foreach (var fragment in SensitiveNameFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Mascara, em um texto livre, e-mails, credenciais <c>Bearer</c>/<c>Basic</c> e o valor de todo par
    /// <c>nome=valor</c> de nome sensível (query string, URL, cabeçalho <c>Cookie</c>, texto de log).
    /// </summary>
    public static string MaskText(string text) => MaskText(text, []);

    /// <summary>
    /// Mascara, em um texto livre, cada valor de <paramref name="sensitiveValues"/> (ex.: os valores das propriedades
    /// sensíveis do mesmo registro, presentes na mensagem formatada), além de e-mails e credenciais.
    /// </summary>
    public static string MaskText(string text, IReadOnlyCollection<string> sensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sensitiveValues);

        var masked = text;
        // Mais longos primeiro: um valor que contém outro é trocado inteiro.
        foreach (var value in sensitiveValues.OrderByDescending(value => value.Length))
        {
            masked = masked.Replace(value, Mask, StringComparison.Ordinal);
        }

        if (masked.Contains('=', StringComparison.Ordinal))
        {
            masked = NameValuePattern().Replace(masked, MaskSensitivePair);
        }

        if (masked.Contains('@', StringComparison.Ordinal))
        {
            masked = EmailPattern().Replace(masked, Mask);
        }

        return CredentialPattern().Replace(masked, "$1 " + Mask);
    }

    /// <summary>
    /// Mascara uma lista de propriedades (atributos de log, estado do <c>ILogger</c>). Devolve <see langword="null"/>
    /// quando nada mudou; os valores originais das propriedades sensíveis vão para <paramref name="sensitiveValues"/>
    /// (para mascarar a mensagem formatada).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>>? MaskProperties(
        IReadOnlyList<KeyValuePair<string, object?>> properties, ICollection<string> sensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(sensitiveValues);

        List<KeyValuePair<string, object?>>? masked = null;
        for (var index = 0; index < properties.Count; index++)
        {
            var (name, value) = properties[index];
            var replacement = MaskProperty(name, value, sensitiveValues);
            if (ReferenceEquals(replacement, value))
            {
                masked?.Add(properties[index]);
                continue;
            }

            masked ??= [.. properties.Take(index)];
            masked.Add(new KeyValuePair<string, object?>(name, replacement));
        }

        return masked;
    }

    /// <summary>Texto de um valor (cultura invariante), como os exportadores o serializam.</summary>
    public static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static object? MaskProperty(string name, object? value, ICollection<string> sensitiveValues)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSensitiveName(name))
        {
            if (Text(value) is { Length: > 0 } original)
            {
                sensitiveValues.Add(original);
            }

            return Mask;
        }

        // Tipos de valor (números, datas, Guid, enums) não carregam e-mail nem credencial.
        if (value.GetType().IsValueType)
        {
            return value;
        }

        var text = Text(value);
        if (text is null)
        {
            return value;
        }

        var maskedText = MaskText(text);
        return string.Equals(maskedText, text, StringComparison.Ordinal) ? value : maskedText;
    }

    // Par nome=valor (ex.: access_token=..., em query string); o nome pode vir percent-encoded.
    private static string MaskSensitivePair(Match pair)
    {
        var name = pair.Groups["name"].Value;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(name);
        }
        catch (UriFormatException)
        {
            decoded = name;
        }

        return IsSensitiveName(decoded) ? $"{name}={Mask}" : pair.Value;
    }

    [GeneratedRegex(@"(?<name>[A-Za-z0-9_.%\-\[\]]+)=(?<value>[^&\s#;,""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex NameValuePattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialPattern();
}
