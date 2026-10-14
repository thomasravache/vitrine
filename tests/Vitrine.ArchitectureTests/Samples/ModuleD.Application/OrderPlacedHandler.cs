using ModuleC.IntegrationEvents;

namespace ModuleD.Application;

/// <summary>Módulo D consome o contrato público de C (permitido isoladamente; fecha um ciclo com C).</summary>
public sealed class OrderPlacedHandler(string source)
{
    public string Handle(OrderPlaced message) =>
        $"{source}:{(message ?? throw new ArgumentNullException(nameof(message))).OrderId}";
}
