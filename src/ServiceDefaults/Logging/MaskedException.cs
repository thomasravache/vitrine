namespace Vitrine.ServiceDefaults.Logging;

/// <summary>
/// Cópia mascarada de uma exceção para a telemetria (ADR-0006). O exportador OTLP lê <c>exception.type</c> do tipo,
/// <c>exception.message</c> de <see cref="Exception.Message"/> e <c>exception.stacktrace</c> de
/// <see cref="ToString"/>: a mensagem traz o tipo original (<c>System.InvalidOperationException: ...</c>), e
/// <see cref="ToString"/>/<see cref="StackTrace"/> devolvem o texto original (tipo, mensagem, pilha e exceções
/// internas) já mascarado. A exceção original não é exposta (<see cref="Exception.InnerException"/> nula).
/// </summary>
public sealed class MaskedException : Exception
{
    private readonly string _text;
    private readonly string? _stackTrace;

    public MaskedException()
        : this(string.Empty, string.Empty, null)
    {
    }

    public MaskedException(string message)
        : this(message, message, null)
    {
    }

    public MaskedException(string message, Exception innerException)
        : base(message, innerException)
    {
        _text = message;
    }

    private MaskedException(string message, string text, string? stackTrace)
        : base(message)
    {
        _text = text;
        _stackTrace = stackTrace;
    }

    /// <summary>Nome completo do tipo da exceção original.</summary>
    public string OriginalType { get; private init; } = string.Empty;

    public override string? StackTrace => _stackTrace;

    /// <summary>
    /// Devolve uma <see cref="MaskedException"/> quando o texto de <paramref name="exception"/> contém dado sensível
    /// (e-mail, credencial, par nome=valor sensível ou um de <paramref name="sensitiveValues"/>); senão, a própria.
    /// </summary>
    public static Exception MaskIfNeeded(Exception exception, IReadOnlyCollection<string> sensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(sensitiveValues);

        var text = exception.ToString();
        var maskedText = SensitiveDataMasker.MaskText(text, sensitiveValues);
        if (string.Equals(text, maskedText, StringComparison.Ordinal))
        {
            return exception;
        }

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var message = SensitiveDataMasker.MaskText(exception.Message, sensitiveValues);
        var stackTrace = exception.StackTrace is { } trace ? SensitiveDataMasker.MaskText(trace, sensitiveValues) : null;
        return new MaskedException($"{type}: {message}", maskedText, stackTrace) { OriginalType = type };
    }

    public override string ToString() => _text;
}
