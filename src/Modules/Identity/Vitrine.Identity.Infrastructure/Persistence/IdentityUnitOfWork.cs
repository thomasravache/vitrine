using Microsoft.EntityFrameworkCore.Storage;
using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Domain.Workspaces;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.Identity.Infrastructure.Persistence;

/// <summary>
/// Transação do <see cref="IdentityModuleDbContext"/> com o outbox do Wolverine (ADR-0014): as mensagens publicadas
/// são gravadas na mesma transação e só seguem para entrega depois do commit.
/// </summary>
internal sealed class IdentityUnitOfWork(IDbContextOutbox<IdentityModuleDbContext> outbox) : IIdentityUnitOfWork, IWorkspaceRepository
{
    public async Task<IIdentityTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new Transaction(outbox, await outbox.DbContext.Database.BeginTransactionAsync(cancellationToken));

    public Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : class =>
        outbox.PublishAsync(integrationEvent).AsTask();

    public void Add(Workspace workspace) => outbox.DbContext.Workspaces.Add(workspace);

    public Task<Workspace?> FindByMemberAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");

    private sealed class Transaction(IDbContextOutbox<IdentityModuleDbContext> outbox, IDbContextTransaction transaction)
        : IIdentityTransaction
    {
        // Grava, confirma a transação aberta e libera o outbox.
        public Task CommitAsync(CancellationToken cancellationToken) =>
            outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
