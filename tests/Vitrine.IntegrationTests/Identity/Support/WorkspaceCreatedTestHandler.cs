using System.Collections.Concurrent;
using Vitrine.Identity.IntegrationEvents;

namespace Vitrine.IntegrationTests.Identity.Support;

/// <summary>
/// Assinante de teste de <see cref="WorkspaceCreated"/> (contrato público do Identity), descoberto pelo Wolverine
/// em <see cref="IdentityHost"/>. Registra cada mensagem recebida e pode ser segurado (<see cref="Hold"/>) para que
/// a mensagem permaneça no armazenamento durável enquanto o teste a inspeciona.
/// </summary>
public static class WorkspaceCreatedTestHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<WorkspaceCreated>> Received = new();
    private static TaskCompletionSource _gate = Released();

    /// <summary>Mensagens recebidas para o workspace.</summary>
    public static IReadOnlyList<WorkspaceCreated> ReceivedFor(Guid workspaceId) =>
        Received.TryGetValue(workspaceId, out var messages) ? [.. messages] : [];

    /// <summary>Segura o processamento até o retorno ser descartado (testes da coleção rodam em sequência).</summary>
    public static IDisposable Hold()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _gate, gate);
        return new Release(gate);
    }

    public static async Task Handle(WorkspaceCreated message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await Volatile.Read(ref _gate).Task.WaitAsync(cancellationToken);
        Received.GetOrAdd(message.WorkspaceId, _ => new ConcurrentQueue<WorkspaceCreated>()).Enqueue(message);
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
