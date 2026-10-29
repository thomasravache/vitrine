using Vitrine.ServiceDefaults.Logging;

namespace Vitrine.Api.Observability;

/// <summary>
/// Decorador de um <see cref="ILoggerProvider"/> de terceiro (o do Sentry): entrega ao logger interno as propriedades
/// do registro já mascaradas e a mensagem formatada mascarada (ADR-0006). O Sentry transforma essas propriedades em
/// tags do evento e a mensagem em <c>logentry</c>/breadcrumb; mascarar antes evita que o valor original chegue lá.
/// A exceção segue original (o Sentry precisa da pilha real); o <c>BeforeSend</c> mascara o texto dela, inclusive os
/// valores das propriedades sensíveis do registro (<see cref="CurrentSensitiveValues"/>), como no pipeline OTLP.
/// O apelido "Sentry" mantém a configuração <c>Logging:Sentry:LogLevel</c> valendo para o decorador.
/// </summary>
[ProviderAlias("Sentry")]
internal sealed class MaskingLoggerProvider(ILoggerProvider inner) : ILoggerProvider, ISupportExternalScope
{
    [ThreadStatic]
    private static IReadOnlyCollection<string>? _currentSensitiveValues;

    /// <summary>
    /// Valores originais das propriedades sensíveis do registro que está sendo entregue ao Sentry nesta thread. O SDK
    /// chama <c>BeforeSend</c>/<c>BeforeBreadcrumb</c> de forma síncrona dentro do <c>Log</c>, e eles usam esta lista
    /// para mascarar o mesmo valor repetido na exceção (SPEC-0013).
    /// </summary>
    internal static IReadOnlyCollection<string> CurrentSensitiveValues => _currentSensitiveValues ?? [];

    public ILogger CreateLogger(string categoryName) => new MaskingLogger(inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (inner is ISupportExternalScope supportsScopes)
        {
            supportsScopes.SetScopeProvider(scopeProvider);
        }
    }

    public void Dispose() => inner.Dispose();

    private sealed class MaskingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }

            var sensitiveValues = new List<string>();
            IReadOnlyList<KeyValuePair<string, object?>> properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            properties = SensitiveDataMasker.MaskProperties(properties, sensitiveValues) ?? properties;
            var message = SensitiveDataMasker.MaskText(formatter(state, exception), sensitiveValues);
            var previous = _currentSensitiveValues;
            _currentSensitiveValues = sensitiveValues;
            try
            {
                inner.Log(logLevel, eventId, new MaskedState(properties, message), exception, static (masked, _) => masked.Message);
            }
            finally
            {
                _currentSensitiveValues = previous;
            }
        }
    }

    /// <summary>Estado mascarado: as propriedades (com <c>{OriginalFormat}</c>) e a mensagem já formatada.</summary>
    private sealed class MaskedState(IReadOnlyList<KeyValuePair<string, object?>> properties, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;

        public int Count => properties.Count;

        public KeyValuePair<string, object?> this[int index] => properties[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => properties.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}
