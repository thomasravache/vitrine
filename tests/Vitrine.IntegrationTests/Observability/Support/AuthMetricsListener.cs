using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Vitrine.IntegrationTests.Observability.Support;

/// <summary>
/// <see cref="MeterListener"/> de teste para o meter <c>Vitrine.Identity</c>: soma as medições de cada instrumento por
/// valor da tag <c>result</c> (ou sem tag). Os testes da coleção rodam em sequência, então só a Api do teste mede.
/// </summary>
internal sealed class AuthMetricsListener : IDisposable
{
    public const string MeterName = "Vitrine.Identity";
    public const string Logins = "vitrine.auth.logins";
    public const string Lockouts = "vitrine.auth.lockouts";
    public const string Signups = "vitrine.auth.signups";

    private const string NoTag = "";

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(string Instrument, string Result), double> _totals = new();
    private readonly ConcurrentDictionary<string, byte> _instruments = new();

    public AuthMetricsListener()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MeterName)
            {
                _instruments.TryAdd(instrument.Name, 0);
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<decimal>((instrument, value, tags, _) => Record(instrument, (double)value, tags));
        _listener.Start();
    }

    /// <summary>Instrumentos do meter <c>Vitrine.Identity</c> já publicados.</summary>
    public IReadOnlyCollection<string> Instruments => [.. _instruments.Keys];

    /// <summary>Soma das medições do instrumento com a tag <c>result</c> informada (nula = qualquer/sem tag).</summary>
    public double Total(string instrument, string? result = null) =>
        _totals.Where(entry => entry.Key.Instrument == instrument && (result is null || entry.Key.Result == result))
            .Sum(entry => entry.Value);

    /// <summary>Valores da tag <c>result</c> já medidos no instrumento.</summary>
    public IReadOnlyCollection<string> Results(string instrument) =>
        [.. _totals.Keys.Where(key => key.Instrument == instrument && key.Result != NoTag).Select(key => key.Result).Distinct()];

    public string Describe() =>
        "Instrumentos: [" + string.Join(", ", Instruments) + "]; medições: ["
        + string.Join(", ", _totals.Select(entry => $"{entry.Key.Instrument}{{result={entry.Key.Result}}}={entry.Value}")) + "]";

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = NoTag;
        foreach (var tag in tags)
        {
            if (tag.Key == "result")
            {
                result = tag.Value?.ToString() ?? NoTag;
            }
        }

        _totals.AddOrUpdate((instrument.Name, result), value, (_, total) => total + value);
    }
}
