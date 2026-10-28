using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vitrine.IntegrationTests.Observability.Support;

/// <summary><see cref="ILoggerProvider"/> de teste: guarda nível, categoria, mensagem e exceção de todo registro do host.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLog> _records = new();

    public IReadOnlyList<RecordedLog> Records => [.. _records];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _records);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<RecordedLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            records.Enqueue(new RecordedLog(logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}

/// <summary>Registro capturado por <see cref="RecordingLoggerProvider"/>.</summary>
internal sealed record RecordedLog(LogLevel Level, string Category, string Message, string? Exception)
{
    public override string ToString() => $"[{Level}] {Category}: {Message}{(Exception is null ? string.Empty : "\n" + Exception)}";
}
