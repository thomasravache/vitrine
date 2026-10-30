using System.Collections.Concurrent;
using Vitrine.MercadoLivre.IntegrationEvents;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Assinante de teste dos eventos de integração do MercadoLivre (contrato v1), descoberto pelo Wolverine no host de
/// teste. Registra cada mensagem por conta e pode ser segurado (<see cref="Hold"/>) para a mensagem ficar no
/// armazenamento durável enquanto o teste a inspeciona.
/// </summary>
public static class MercadoLivreEventsTestHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<object>> Received = new();
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<MercadoLivreItemChanged>> ItemChanges = new(StringComparer.Ordinal);
    private static TaskCompletionSource _gate = Released();

    /// <summary>Mensagens recebidas para a conta, do tipo pedido.</summary>
    public static IReadOnlyList<T> ReceivedFor<T>(Guid accountId) =>
        Received.TryGetValue(accountId, out var messages) ? [.. messages.OfType<T>()] : [];

    /// <summary><c>MercadoLivreItemChanged</c> recebidos para a notificação (<c>notificationId</c>), com ou sem conta.</summary>
    public static IReadOnlyList<MercadoLivreItemChanged> ItemChangesFor(string notificationId) =>
        ItemChanges.TryGetValue(notificationId, out var messages) ? [.. messages] : [];

    /// <summary>Segura o processamento até o retorno ser descartado (testes da coleção rodam em sequência).</summary>
    public static IDisposable Hold()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _gate, gate);
        return new Release(gate);
    }

    public static async Task Handle(MercadoLivreAccountConnected message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        Received.GetOrAdd(message.AccountId, _ => new ConcurrentQueue<object>()).Enqueue(message);
    }

    public static async Task Handle(MercadoLivreAccountDisconnected message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        Received.GetOrAdd(message.AccountId, _ => new ConcurrentQueue<object>()).Enqueue(message);
    }

    public static async Task Handle(MercadoLivreAccountReauthorizationRequired message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        Received.GetOrAdd(message.AccountId, _ => new ConcurrentQueue<object>()).Enqueue(message);
    }

    public static async Task Handle(MercadoLivreItemChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        Received.GetOrAdd(message.AccountId, _ => new ConcurrentQueue<object>()).Enqueue(message);
        ItemChanges.GetOrAdd(message.NotificationId, _ => new ConcurrentQueue<MercadoLivreItemChanged>()).Enqueue(message);
    }

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private sealed class Release(TaskCompletionSource gate) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref _gate, Released());
            gate.TrySetResult();
        }
    }
}
