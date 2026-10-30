using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Infrastructure.Diagnostics;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;
using Vitrine.MercadoLivre.PublicApi;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <see cref="IMercadoLivreAccessTokenProvider"/> sobre a tabela <c>accounts</c> (SPEC-0021). Singleton: cada operação
/// usa um escopo de DI próprio (o handler HTTP vive mais que uma requisição). A renovação acontece numa transação com a
/// linha da conta travada (<c>SELECT … FOR UPDATE</c>), relendo a conta depois do lock: chamadas concorrentes de qualquer
/// processo esperam e usam o token novo, e o <c>POST /oauth/token</c> sai no máximo uma vez.
/// </summary>
internal sealed class MercadoLivreAccessTokenProvider(
    IServiceScopeFactory scopes,
    TokenRefreshPolicy policy,
    MercadoLivreTokenRefresher refresher,
    MercadoLivreMetrics metrics,
    TimeProvider clock) : IMercadoLivreAccessTokenProvider
{
    public async Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MercadoLivreModuleDbContext>();
            var account = EnsureUsable(accountId, await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken));
            if (!policy.ShouldRefresh(account.AccessTokenExpiresAt, forced: false))
            {
                return account.AccessToken;
            }
        }

        return await RenewUnderLockAsync(accountId, rejectedAccessToken: null, cancellationToken);
    }

    public Task<string> RenewAfterRejectionAsync(Guid accountId, string rejectedAccessToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejectedAccessToken);
        return RenewUnderLockAsync(accountId, rejectedAccessToken, cancellationToken);
    }

    /// <summary>
    /// O ML rejeitou de novo (401) o token recém-renovado: se ele ainda é o da conta, a conta vai para
    /// <c>reauthorization_required</c> (tokens apagados, evento no outbox). Sempre termina com
    /// <see cref="MercadoLivreReauthorizationRequiredException"/>.
    /// </summary>
    public async Task RequireReauthorizationAsync(Guid accountId, string rejectedAccessToken, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<MercadoLivreModuleDbContext>>();
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(cancellationToken);
        var account = await LockAsync(outbox.DbContext, accountId, cancellationToken);
        if (account is null)
        {
            throw new MercadoLivreAccountNotFoundException(accountId);
        }

        if (account.Status == MercadoLivreAccountStatus.Connected && account.AccessToken == rejectedAccessToken)
        {
            await MarkReauthorizationRequiredAsync(outbox, account, cancellationToken);
        }

        throw new MercadoLivreReauthorizationRequiredException(accountId);
    }

    private async Task<string> RenewUnderLockAsync(Guid accountId, string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<MercadoLivreModuleDbContext>>();
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(cancellationToken);

        // Relida sob o lock: outra chamada pode ter renovado (ou invalidado) enquanto esta esperava.
        var account = EnsureUsable(accountId, await LockAsync(outbox.DbContext, accountId, cancellationToken));
        var forced = rejectedAccessToken is not null;
        if (forced ? account.AccessToken != rejectedAccessToken : !policy.ShouldRefresh(account.AccessTokenExpiresAt, forced: false))
        {
            return account.AccessToken;
        }

        switch (await refresher.RefreshAsync(account.RefreshToken, cancellationToken))
        {
            case TokenRefresh.Renewed renewed:
                account.RenewTokens(renewed.Tokens, clock.GetUtcNow());
                await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
                metrics.TokenRefreshed(TokenRefreshOutcome.Renewed);
                return account.AccessToken;

            case TokenRefresh.Rejected:
                await MarkReauthorizationRequiredAsync(outbox, account, cancellationToken);
                metrics.TokenRefreshed(TokenRefreshOutcome.ReauthorizationRequired);
                throw new MercadoLivreReauthorizationRequiredException(accountId);

            default:
                // Nada muda (a transação é desfeita); o refresh não é retentado.
                metrics.TokenRefreshed(TokenRefreshOutcome.Unavailable);
                throw new MercadoLivreUnavailableException(accountId, "renovação do token falhou");
        }
    }

    // Tokens apagados e evento v1 no outbox, na mesma transação (ADR-0014).
    private async Task MarkReauthorizationRequiredAsync(
        IDbContextOutbox<MercadoLivreModuleDbContext> outbox, MercadoLivreAccount account, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        account.RequireReauthorization(now);
        await outbox.PublishAsync(new MercadoLivreAccountReauthorizationRequired(account.Id, account.WorkspaceId, account.MlUserId, now));
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    private static async Task<MercadoLivreAccount?> LockAsync(MercadoLivreModuleDbContext db, Guid accountId, CancellationToken cancellationToken)
    {
        var rows = await db.Accounts
            .FromSql($"select * from mercadolivre.accounts where id = {accountId} for update")
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>A conta existe e está conectada; senão, a exceção do contrato.</summary>
    internal static MercadoLivreAccount EnsureUsable(Guid accountId, MercadoLivreAccount? account) =>
        account switch
        {
            null => throw new MercadoLivreAccountNotFoundException(accountId),
            { Status: MercadoLivreAccountStatus.ReauthorizationRequired } => throw new MercadoLivreReauthorizationRequiredException(accountId),
            _ => account,
        };
}
