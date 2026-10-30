namespace Vitrine.MercadoLivreSimulator;

/// <summary>
/// Simulador do Mercado Livre (SPEC-0024) — scaffold: sem endpoints ainda. <c>Program</c> com namespace (sem instruções
/// de nível superior) para não colidir com o <c>Program</c> público da Api nos projetos de teste que referenciam os dois.
/// </summary>
internal static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.Run();
    }
}
