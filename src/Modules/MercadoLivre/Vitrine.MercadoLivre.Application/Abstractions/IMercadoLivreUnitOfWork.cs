namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>
/// Unidade de trabalho do módulo: uma transação no schema <c>mercadolivre</c> em que as mudanças e os eventos de
/// integração publicados (outbox — ADR-0014) são confirmados juntos, ou descartados juntos.
/// </summary>
public interface IMercadoLivreUnitOfWork
{
    /// <summary>Abre a transação; descartá-la sem <see cref="IMercadoLivreTransaction.CommitAsync"/> desfaz tudo.</summary>
    Task<IMercadoLivreTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Publica um evento de integração pelo outbox da transação aberta.</summary>
    Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : class;
}

/// <summary>Transação aberta por <see cref="IMercadoLivreUnitOfWork.BeginTransactionAsync"/>.</summary>
public interface IMercadoLivreTransaction : IAsyncDisposable
{
    /// <summary>
    /// Grava as mudanças, confirma a transação e libera as mensagens do outbox para entrega. Lança
    /// <see cref="ConcurrentChangeException"/> quando outra requisição mudou a mesma linha antes (ou gravou a mesma
    /// chave única).
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Conflito de concorrência na gravação: nada foi confirmado.</summary>
public sealed class ConcurrentChangeException : Exception
{
    public ConcurrentChangeException()
    {
    }

    public ConcurrentChangeException(string message)
        : base(message)
    {
    }

    public ConcurrentChangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
