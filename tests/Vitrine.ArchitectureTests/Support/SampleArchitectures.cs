using ArchUnitNET.Domain;
using ArchUnitNET.Loader;

namespace Vitrine.ArchitectureTests.Support;

/// <summary>Arquiteturas montadas só com os assemblies de amostra de <c>Samples/</c> (UT-03, UT-04).</summary>
internal static class SampleArchitectures
{
    private static readonly Lazy<Architecture> LayerViolationArchitecture = new(() => new ArchLoader()
        .LoadAssemblies(typeof(Sample.Domain.Order).Assembly, typeof(Sample.Infrastructure.OrderRepository).Assembly)
        .Build());

    private static readonly Lazy<Architecture> ModuleViolationArchitecture = new(() => new ArchLoader()
        .LoadAssemblies(typeof(ModuleA.Application.PlaceOrderCommand).Assembly, typeof(ModuleB.Application.GetCustomerQuery).Assembly)
        .Build());

    private static readonly Lazy<Architecture> ModuleCycleArchitecture = new(() => new ArchLoader()
        .LoadAssemblies(
            typeof(ModuleC.Application.StockReservedHandler).Assembly,
            typeof(ModuleC.IntegrationEvents.OrderPlaced).Assembly,
            typeof(ModuleD.Application.OrderPlacedHandler).Assembly,
            typeof(ModuleD.IntegrationEvents.StockReserved).Assembly)
        .Build());

    private static readonly Lazy<Architecture> CommonViolationArchitecture = new(() => new ArchLoader()
        .LoadAssemblies(typeof(Common.Clock.SystemClock).Assembly, typeof(ModuleB.Application.GetCustomerQuery).Assembly)
        .Build());

    /// <summary><c>Sample.Domain</c> referencia <c>Sample.Infrastructure</c>.</summary>
    public static Architecture LayerViolation => LayerViolationArchitecture.Value;

    /// <summary><c>ModuleA.Application</c> referencia <c>ModuleB.Application</c>.</summary>
    public static Architecture ModuleViolation => ModuleViolationArchitecture.Value;

    /// <summary>ModuleC e ModuleD dependem um do outro só pelos contratos públicos (ciclo entre módulos).</summary>
    public static Architecture ModuleCycle => ModuleCycleArchitecture.Value;

    /// <summary><c>Common.Clock</c> referencia <c>ModuleB.Application</c>.</summary>
    public static Architecture CommonViolation => CommonViolationArchitecture.Value;
}
