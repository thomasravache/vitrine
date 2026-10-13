namespace ModuleB.Application;

public sealed class GetCustomerQuery(string prefix)
{
    public string Execute(string customerId) => $"{prefix}:{customerId}";
}
