namespace Vitrine.MercadoLivreSimulator;

/// <summary>
/// Marcador público do assembly do simulador, usado como <c>TEntryPoint</c> do <c>WebApplicationFactory</c> nos testes
/// (o <c>Program</c> do simulador é interno e não colide com o da Api).
/// </summary>
public sealed class SimulatorEntryPoint
{
    private SimulatorEntryPoint()
    {
    }
}
