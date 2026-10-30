using Vitrine.Listings.Application.Sync;
using Wolverine;

namespace Vitrine.Listings.Infrastructure.Messaging;

/// <summary>Enfileira <see cref="SyncListings"/> na fila local durável do Wolverine (ADR-0014).</summary>
internal sealed class WolverineSyncScheduler(IMessageBus bus) : ISyncScheduler
{
    public Task EnqueueAsync(SyncListings command, CancellationToken cancellationToken) => bus.PublishAsync(command).AsTask();
}
