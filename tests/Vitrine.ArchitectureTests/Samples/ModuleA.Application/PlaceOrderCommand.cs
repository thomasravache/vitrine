using ModuleB.Application;

namespace ModuleA.Application;

/// <summary>Caso de uso do módulo A que usa o Application do módulo B (violação de isolamento).</summary>
public sealed class PlaceOrderCommand(GetCustomerQuery customers)
{
    public string Execute(string customerId) => customers.Execute(customerId);
}
