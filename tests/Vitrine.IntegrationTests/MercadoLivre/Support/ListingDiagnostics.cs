using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Vitrine.MercadoLivre.Infrastructure;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Ouve <c>vitrine.mercadolivre.listing_edits{field,result,code}</c> (SPEC-0033) no meter do módulo criado pelo
/// <see cref="IMeterFactory"/> do host do teste.
/// </summary>
internal sealed class ListingEditsListener : IDisposable
{
    public const string Instrument = "vitrine.mercadolivre.listing_edits";

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(string Field, string Result, string Code), long> _totals = new();

    public ListingEditsListener(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MercadoLivreModule.MeterName
                && instrument.Name == Instrument
                && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Record(value, tags));
        _listener.SetMeasurementEventCallback<int>((_, value, tags, _) => Record(value, tags));
        _listener.Start();
    }

    /// <summary>Soma das medições com os valores de tag pedidos (nulo = qualquer).</summary>
    public long Total(string? field = null, string? result = null, string? code = null) =>
        _totals.Where(entry => (field is null || entry.Key.Field == field)
                && (result is null || entry.Key.Result == result)
                && (code is null || entry.Key.Code == code))
            .Sum(entry => entry.Value);

    public string Describe() =>
        "[" + string.Join(", ", _totals.Select(entry => $"{{field={entry.Key.Field}, result={entry.Key.Result}, code={entry.Key.Code}}}={entry.Value}")) + "]";

    public void Dispose() => _listener.Dispose();

    private void Record(long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string field = string.Empty, result = string.Empty, code = string.Empty;
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "field":
                    field = tag.Value?.ToString() ?? string.Empty;
                    break;
                case "result":
                    result = tag.Value?.ToString() ?? string.Empty;
                    break;
                case "code":
                    code = tag.Value?.ToString() ?? string.Empty;
                    break;
            }
        }

        _totals.AddOrUpdate((field, result, code), value, (_, total) => total + value);
    }
}

/// <summary>
/// Spans do <c>ActivitySource</c> <see cref="MercadoLivreModule.ActivitySourceName"/> de uma conta: o
/// <c>ml.endpoint</c> de cada chamada ao ML (o listener é do processo; filtra pelo <c>ml.account_id</c>).
/// </summary>
internal sealed class ApiSpansListener : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<(string? AccountId, string? Endpoint)> _spans = new();

    public ApiSpansListener()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MercadoLivreModule.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _spans.Enqueue((activity.GetTagItem("ml.account_id") as string, activity.GetTagItem("ml.endpoint") as string)),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary><c>ml.endpoint</c> dos spans da conta, em ordem de término.</summary>
    public IReadOnlyList<string> EndpointsFor(Guid accountId) =>
        [.. _spans.Where(span => span.AccountId == accountId.ToString("D")).Select(span => span.Endpoint ?? string.Empty)];

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// <see cref="TimeProvider"/> de teste: relógio e timers reais, mas registra o prazo de cada timer criado (ex.: a espera
/// de 2 s do editor depois de um 409).
/// </summary>
internal sealed class RecordingTimeProvider : TimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _requested = new();

    /// <summary>Prazos pedidos aos timers, em ordem.</summary>
    public IReadOnlyList<TimeSpan> Requested => [.. _requested];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            _requested.Enqueue(dueTime);
        }

        return System.CreateTimer(callback, state, dueTime, period);
    }
}

/// <summary><see cref="ILoggerProvider"/> de teste que guarda nível, categoria, <see cref="EventId"/> e mensagem.</summary>
internal sealed class EventRecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<EventLog> _records = new();

    public IReadOnlyList<EventLog> Records => [.. _records];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _records);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<EventLog> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            records.Enqueue(new EventLog(logLevel, category, eventId, formatter(state, exception), exception?.ToString()));
        }
    }
}

/// <summary>Registro capturado por <see cref="EventRecordingLoggerProvider"/>.</summary>
internal sealed record EventLog(LogLevel Level, string Category, EventId EventId, string Message, string? Exception)
{
    public override string ToString() =>
        $"[{Level}] {Category} ({EventId.Id}:{EventId.Name}): {Message}{(Exception is null ? string.Empty : "\n" + Exception)}";
}
