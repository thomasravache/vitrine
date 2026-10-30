using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
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
/// processo esperam (até <see cref="LockTimeout"/>) e usam o token novo, e o <c>POST /oauth/token</c> sai no máximo uma
/// vez: do envio dele até o commit nada depende do cancelamento da tentativa HTTP, e qualquer falha vira
/// <see cref="MercadoLivreUnavailableException"/> (nunca retentada pelo pipeline).
/// </summary>
/// <remarks>
/// Não pode ser chamado dentro de uma transação que já trave linhas de <c>mercadolivre.accounts</c>: a renovação usa
/// outra conexão e esperaria por esse lock até o <see cref="LockTimeout"/>.
/// </remarks>
internal sealed partial class MercadoLivreAccessTokenProvider(
    IServiceScopeFactory scopes,
    TokenRefreshPolicy policy,
    MercadoLivreTokenRefresher refresher,
    MercadoLivreMetrics metrics,
    TimeProvider clock,
    ILogger<MercadoLivreAccessTokenProvider> logger) : IMercadoLivreAccessTokenProvider
{
    /// <summary>Espera máxima pelo lock da conta antes de desistir com <see cref="MercadoLivreUnavailableException"/>.</summary>
    public const string LockTimeout = "15s";

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
    /// O ML rejeitou de novo (401) o token recém-renovado. Se ele ainda é o da conta, a conta vai para
    /// <c>reauthorization_required</c> (tokens apagados, evento no outbox, log Warning) e lança
    /// <see cref="MercadoLivreReauthorizationRequiredException"/>; se a conta já estava assim, também. Se outra renovação
    /// já substituiu o token rejeitado, lança <see cref="MercadoLivreUnavailableException"/> (a conta segue conectada).
    /// </summary>
    public async Task RequireReauthorizationAsync(Guid accountId, string rejectedAccessToken, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<MercadoLivreModuleDbContext>>();
        await using var transaction = await outbox.DbContext.Database.BeginTransactionAsync(cancellationToken);
        var account = await LockAsync(outbox.DbContext, accountId, cancellationToken)
            ?? throw new MercadoLivreAccountNotFoundException(accountId);

        if (account.Status == MercadoLivreAccountStatus.ReauthorizationRequired)
        {
            throw new MercadoLivreReauthorizationRequiredException(accountId);
        }

        if (account.AccessToken != rejectedAccessToken)
        {
            throw new MercadoLivreUnavailableException(accountId, "o token rejeitado já foi substituído por outra renovação");
        }

        await MarkReauthorizationRequiredAsync(outbox, account, CancellationToken.None);
        Log.RejectedAfterRenewal(logger, accountId);
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

        // Daqui até o commit, nada depende do cancelamento da tentativa HTTP: o refresh token é de uso único, então um
        // POST enviado nunca é abandonado para ser repetido (o cliente mercadolivre-oauth limita a espera a 10 s).
        var refresh = await refresher.RefreshAsync(account.RefreshToken, CancellationToken.None);
        try
        {
            switch (refresh)
            {
                case TokenRefresh.Renewed renewed:
                    account.RenewTokens(renewed.Tokens, clock.GetUtcNow());
                    try
                    {
                        await outbox.SaveChangesAndFlushMessagesAsync(CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is DbUpdateException or NpgsqlException)
                    {
                        // O ML já consumiu o refresh token antigo: sem gravar o novo, a conta não renova mais. Sem nova
                        // tentativa aqui: depois do erro a transação (e o lock) do PostgreSQL ficou abortada.
                        Log.RenewedButNotSaved(logger, accountId, exception);
                        throw;
                    }

                    metrics.TokenRefreshed(TokenRefreshOutcome.Renewed);
                    return account.AccessToken;

                case TokenRefresh.Rejected:
                    await MarkReauthorizationRequiredAsync(outbox, account, CancellationToken.None);
                    metrics.TokenRefreshed(TokenRefreshOutcome.ReauthorizationRequired);
                    Log.RefreshRejected(logger, accountId);
                    throw new MercadoLivreReauthorizationRequiredException(accountId);

                default:
                    // Nada muda (a transação é desfeita); o refresh não é retentado.
                    Log.RefreshFailed(logger, accountId);
                    metrics.TokenRefreshed(TokenRefreshOutcome.Unavailable);
                    throw new MercadoLivreUnavailableException(accountId, "renovação do token falhou");
            }
        }
        catch (Exception exception) when (exception is DbUpdateException or NpgsqlException)
        {
            // Falha ao gravar o resultado: nada confirmado; também não é retentado.
            Log.RefreshFailed(logger, accountId);
            metrics.TokenRefreshed(TokenRefreshOutcome.Unavailable);
            throw new MercadoLivreUnavailableException(accountId, "falha ao gravar a renovação do token", exception);
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

    // Trava a linha da conta na transação aberta, esperando no máximo LockTimeout (estouro: Unavailable).
    private static async Task<MercadoLivreAccount?> LockAsync(MercadoLivreModuleDbContext db, Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync($"set local lock_timeout = '{LockTimeout}'", cancellationToken);
            var rows = await db.Accounts
                .FromSql($"select * from mercadolivre.accounts where id = {accountId} for update")
                .ToListAsync(cancellationToken);
            return rows.Count == 0 ? null : rows[0];
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new MercadoLivreUnavailableException(accountId, "conta travada por outra renovação", exception);
        }
    }

    /// <summary>A conta existe e está conectada; senão, a exceção do contrato.</summary>
    internal static MercadoLivreAccount EnsureUsable(Guid accountId, MercadoLivreAccount? account) =>
        account switch
        {
            null => throw new MercadoLivreAccountNotFoundException(accountId),
            { Status: MercadoLivreAccountStatus.ReauthorizationRequired } => throw new MercadoLivreReauthorizationRequiredException(accountId),
            _ => account,
        };

    /// <summary>Mensagens de log (com o accountId; nunca tokens).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "Conta do Mercado Livre {AccountId} precisa ser reconectada: o ML rejeitou o token renovado (401).")]
        public static partial void RejectedAfterRenewal(ILogger logger, Guid accountId);

        [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Conta do Mercado Livre {AccountId} precisa ser reconectada: o ML recusou o refresh token.")]
        public static partial void RefreshRejected(ILogger logger, Guid accountId);

        [LoggerMessage(EventId = 24, Level = LogLevel.Critical, Message = "Conta do Mercado Livre {AccountId}: renovação obtida mas não gravada; a conta exigirá reconexão.")]
        public static partial void RenewedButNotSaved(ILogger logger, Guid accountId, Exception exception);

        [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "Renovação do token da conta do Mercado Livre {AccountId} falhou; a conta não mudou.")]
        public static partial void RefreshFailed(ILogger logger, Guid accountId);
    }
}
