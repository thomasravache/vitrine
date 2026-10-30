using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Domain.Authorizations;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// Transação do <see cref="MercadoLivreModuleDbContext"/> com o outbox do Wolverine (ADR-0014): as mensagens
/// publicadas são gravadas na mesma transação e só seguem para entrega depois do commit. Também é o repositório de
/// contas e de autorizações pendentes do módulo.
/// </summary>
internal sealed class MercadoLivreUnitOfWork(IDbContextOutbox<MercadoLivreModuleDbContext> outbox)
    : IMercadoLivreUnitOfWork, IMercadoLivreAccounts, IPendingAuthorizations
{
    private MercadoLivreModuleDbContext Db => outbox.DbContext;

    public async Task<IMercadoLivreTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new Transaction(outbox, await Db.Database.BeginTransactionAsync(cancellationToken));

    public Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : class =>
        outbox.PublishAsync(integrationEvent).AsTask();

    public void Add(MercadoLivreAccount account) => Db.Accounts.Add(account);

    public void Remove(MercadoLivreAccount account) => Db.Accounts.Remove(account);

    public Task<MercadoLivreAccount?> FindByMlUserIdAsync(long mlUserId, CancellationToken cancellationToken) =>
        Db.Accounts.FirstOrDefaultAsync(account => account.MlUserId == mlUserId, cancellationToken);

    public Task<MercadoLivreAccount?> FindInWorkspaceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        Db.Accounts.FirstOrDefaultAsync(account => account.Id == accountId && account.WorkspaceId == workspaceId, cancellationToken);

    public async Task<IReadOnlyList<MercadoLivreAccountView>> ListAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await Db.Accounts.AsNoTracking()
            .Where(account => account.WorkspaceId == workspaceId)
            .OrderBy(account => account.ConnectedAt)
            .ThenBy(account => account.Id)
            .Select(account => new MercadoLivreAccountView(
                account.Id, account.MlUserId, account.Nickname, account.SiteId, account.Status, account.ConnectedAt))
            .ToListAsync(cancellationToken);

    public void Add(PendingAuthorization authorization) => Db.PendingAuthorizations.Add(authorization);

    public Task<PendingAuthorization?> FindByStateHashAsync(string stateHash, CancellationToken cancellationToken) =>
        Db.PendingAuthorizations.FirstOrDefaultAsync(authorization => authorization.StateHash == stateHash, cancellationToken);

    private sealed class Transaction(IDbContextOutbox<MercadoLivreModuleDbContext> outbox, IDbContextTransaction transaction)
        : IMercadoLivreTransaction
    {
        // Grava, confirma a transação aberta e libera o outbox.
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ConcurrentChangeException("A linha foi alterada por outra requisição.", exception);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new ConcurrentChangeException("Chave única já gravada por outra requisição.", exception);
            }
        }

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
