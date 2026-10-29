using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Vitrine.ServiceDefaults.Logging;

/// <summary>
/// Console em JSON, uma linha por registro, com <c>trace_id</c>/<c>span_id</c> da atividade corrente e as mesmas
/// regras de mascaramento do pipeline do OpenTelemetry (ADR-0006): a saída do container (<c>docker logs</c>) também
/// não carrega e-mail, senha, token nem cookie.
/// </summary>
internal sealed class MaskedJsonConsoleFormatter : ConsoleFormatter, IDisposable
{
    /// <summary>Nome do formatador (<c>Logging:Console:FormatterName</c>).</summary>
    public const string FormatterName = "vitrine-json";

    private const string OriginalFormat = "{OriginalFormat}";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Logs não são HTML: acentos e aspas saem legíveis; o JSON continua válido.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDisposable? _optionsReload;
    private ConsoleFormatterOptions _options;

    public MaskedJsonConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.CurrentValue;
        _optionsReload = options.OnChange(updated => _options = updated);
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var sensitiveValues = new List<string>();
        var state = logEntry.State as IReadOnlyList<KeyValuePair<string, object?>>;
        if (state is not null)
        {
            state = SensitiveDataMasker.MaskProperties(state, sensitiveValues) ?? state;
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("level", logEntry.LogLevel.ToString());
            writer.WriteString("category", logEntry.Category);
            writer.WriteNumber("event_id", logEntry.EventId.Id);
            if (message is not null)
            {
                writer.WriteString("message", SensitiveDataMasker.MaskText(message, sensitiveValues));
            }

            if (Activity.Current is { } activity)
            {
                writer.WriteString("trace_id", activity.TraceId.ToHexString());
                writer.WriteString("span_id", activity.SpanId.ToHexString());
            }

            if (logEntry.Exception is { } exception)
            {
                writer.WriteString("exception", SensitiveDataMasker.MaskText(exception.ToString(), sensitiveValues));
            }

            if (state is { Count: > 0 })
            {
                writer.WriteStartObject("attributes");
                foreach (var (name, value) in state)
                {
                    writer.WriteString(name == OriginalFormat ? "template" : name, SensitiveDataMasker.Text(value));
                }

                writer.WriteEndObject();
            }

            if (_options.IncludeScopes && scopeProvider is not null)
            {
                WriteScopes(writer, scopeProvider, sensitiveValues);
            }

            writer.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write(Environment.NewLine);
    }

    public void Dispose() => _optionsReload?.Dispose();

    private static void WriteScopes(Utf8JsonWriter writer, IExternalScopeProvider scopeProvider, List<string> sensitiveValues)
    {
        var started = false;
        scopeProvider.ForEachScope(
            (scope, json) =>
            {
                if (!started)
                {
                    json.WriteStartArray("scopes");
                    started = true;
                }

                if (scope is IReadOnlyList<KeyValuePair<string, object?>> properties)
                {
                    json.WriteStartObject();
                    foreach (var (name, value) in SensitiveDataMasker.MaskProperties(properties, sensitiveValues) ?? properties)
                    {
                        json.WriteString(name == OriginalFormat ? "template" : name, SensitiveDataMasker.Text(value));
                    }

                    json.WriteEndObject();
                }
                else if (SensitiveDataMasker.Text(scope) is { } text)
                {
                    json.WriteStringValue(SensitiveDataMasker.MaskText(text, sensitiveValues));
                }
            },
            writer);

        if (started)
        {
            writer.WriteEndArray();
        }
    }
}
