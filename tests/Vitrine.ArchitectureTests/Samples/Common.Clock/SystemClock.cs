using ModuleB.Application;

namespace Common.Clock;

/// <summary>Building block de Common que depende indevidamente de um módulo.</summary>
public sealed class SystemClock(GetCustomerQuery customers)
{
    public string Describe(string customerId) => customers.Execute(customerId);
}
