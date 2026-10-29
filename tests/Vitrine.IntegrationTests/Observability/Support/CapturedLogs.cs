using System.Collections.Concurrent;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Vitrine.IntegrationTests.Observability.Support;

/// <summary>
/// Exportador de logs OpenTelemetry em memória: guarda uma cópia imutável (texto) de cada registro que sai do
/// pipeline — mensagem formatada, corpo, atributos, escopos e exceção —, ou seja, exatamente o que um destino
/// (OTLP/dashboard) receberia depois dos processadores da aplicação.
/// </summary>
internal sealed class CapturedLogs : BaseExporter<LogRecord>
{
    private readonly ConcurrentQueue<CapturedLog> _records = new();

    public IReadOnlyList<CapturedLog> Records => [.. _records];

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        foreach (var record in batch)
        {
            _records.Enqueue(CapturedLog.From(record));
        }

        return ExportResult.Success;
    }
}

/// <summary>Cópia em texto de um <see cref="LogRecord"/> exportado.</summary>
internal sealed record CapturedLog(
    string Category,
    string? FormattedMessage,
    string? Body,
    IReadOnlyList<KeyValuePair<string, string?>> Attributes,
    IReadOnlyList<KeyValuePair<string, string?>> Scopes,
    string? Exception)
{
    public static CapturedLog From(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var attributes = (record.Attributes ?? [])
            .Select(attribute => new KeyValuePair<string, string?>(attribute.Key, Text(attribute.Value)))
            .ToList();

        var scopes = new List<KeyValuePair<string, string?>>();
        record.ForEachScope(
            static (scope, sink) =>
            {
                foreach (var item in scope)
                {
                    sink.Add(new KeyValuePair<string, string?>(item.Key, Text(item.Value)));
                }

                // Escopo sem pares chave/valor (ex.: string simples) também é emitido.
                if (scope.Scope is not IEnumerable<KeyValuePair<string, object?>>)
                {
                    sink.Add(new KeyValuePair<string, string?>(string.Empty, Text(scope.Scope)));
                }
            },
            scopes);

        return new CapturedLog(
            record.CategoryName ?? string.Empty,
            record.FormattedMessage,
            record.Body,
            attributes,
            scopes,
            record.Exception?.ToString());
    }

    /// <summary>Valor do atributo pelo nome (o último, se repetido); nulo quando ausente.</summary>
    public string? Attribute(string key) =>
        Attributes.LastOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal)).Value;

    /// <summary>Todo o texto emitido pelo registro (mensagem, corpo, atributos, escopos, exceção).</summary>
    public string AllText() => string.Join(
        '\n',
        new[] { Category, FormattedMessage, Body, Exception }
            .Concat(Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"))
            .Concat(Scopes.Select(scope => $"{scope.Key}={scope.Value}")));

    private static string? Text(object? value) => value switch
    {
        null => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
