namespace Vitrine.Identity.Application.Abstractions;

/// <summary>
/// Unidade de trabalho do módulo: uma transação no schema <c>identity</c> em que as mudanças e os eventos de
/// integração publicados (outbox — ADR-0014) são confirmados juntos, ou descartados juntos.
/// </summary>
public interface IIdentityUnitOfWork
{
    /// <summary>Abre a transação; descartá-la sem <see cref="IIdentityTransaction.CommitAsync"/> desfaz tudo.</summary>
    Task<IIdentityTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Publica um evento de integração pelo outbox da transação aberta.</summary>
    Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : class;
}

/// <summary>Transação aberta por <see cref="IIdentityUnitOfWork.BeginTransactionAsync"/>.</summary>
public interface IIdentityTransaction : IAsyncDisposable
{
    /// <summary>Grava as mudanças, confirma a transação e libera as mensagens do outbox para entrega.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
