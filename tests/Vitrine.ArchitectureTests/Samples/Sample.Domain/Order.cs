using Sample.Infrastructure;

namespace Sample.Domain;

/// <summary>Entidade de domínio que depende indevidamente da infraestrutura (violação de camada).</summary>
public sealed class Order(string id)
{
    public string Id { get; } = id;

    public void Persist(OrderRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        repository.Save(Id);
    }
}
