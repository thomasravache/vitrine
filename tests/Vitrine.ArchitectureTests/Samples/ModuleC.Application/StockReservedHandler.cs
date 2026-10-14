using ModuleD.IntegrationEvents;

namespace ModuleC.Application;

/// <summary>Módulo C consome o contrato público de D (permitido isoladamente; fecha um ciclo com D).</summary>
public sealed class StockReservedHandler(string source)
{
    public string Handle(StockReserved message) =>
        $"{source}:{(message ?? throw new ArgumentNullException(nameof(message))).OrderId}";
}
