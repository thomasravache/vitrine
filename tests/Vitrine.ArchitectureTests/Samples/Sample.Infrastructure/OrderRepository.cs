namespace Sample.Infrastructure;

public sealed class OrderRepository
{
    public void Save(string orderId) => LastSaved = orderId;

    public string? LastSaved { get; private set; }
}
