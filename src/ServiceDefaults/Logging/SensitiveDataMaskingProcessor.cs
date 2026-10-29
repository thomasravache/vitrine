using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Vitrine.ServiceDefaults.Logging;

/// <summary>
/// Enriquecedor do pipeline de logs do OpenTelemetry (ADR-0006): registrado pelos padrões de serviço antes de
/// qualquer exportador, mascara atributos, mensagem formatada e corpo de cada registro, de modo que o OTLP (Grafana
/// Cloud, dashboard do Aspire) e qualquer exportador acrescentado depois só recebam o texto já mascarado.
/// </summary>
/// <remarks>
/// Escopos não são reescritos (o OpenTelemetry os lê do provedor de escopos na exportação); os escopos do ASP.NET
/// Core carregam caminho da requisição e IDs de correlação, não dados pessoais. A exceção com dado sensível é trocada
/// por uma <see cref="MaskedException"/>.
/// </remarks>
internal sealed class SensitiveDataMaskingProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var sensitiveValues = new List<string>();
        if (data.Attributes is { Count: > 0 } attributes
            && SensitiveDataMasker.MaskProperties(attributes, sensitiveValues) is { } masked)
        {
            data.Attributes = masked;
        }

        if (data.FormattedMessage is { } message)
        {
            data.FormattedMessage = SensitiveDataMasker.MaskText(message, sensitiveValues);
        }

        if (data.Body is { } body)
        {
            data.Body = SensitiveDataMasker.MaskText(body, sensitiveValues);
        }

        // Exceção com dado sensível na mensagem/pilha vira uma cópia mascarada (tipo original na mensagem).
        if (data.Exception is { } exception)
        {
            data.Exception = MaskedException.MaskIfNeeded(exception, sensitiveValues);
        }
    }
}
