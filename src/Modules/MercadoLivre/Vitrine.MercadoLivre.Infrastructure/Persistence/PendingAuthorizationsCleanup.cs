using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// Job diário do módulo (contrato da SPEC-0020): apaga autorizações pendentes expiradas há mais de 1 dia. A primeira
/// execução acontece um dia depois da subida; várias réplicas rodando ao mesmo tempo só repetem um DELETE idempotente.
/// </summary>
internal sealed partial class PendingAuthorizationsCleanup(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<PendingAuthorizationsCleanup> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MercadoLivreModuleDbContext>();
                var limit = clock.GetUtcNow() - Retention;
                var deleted = await db.PendingAuthorizations
                    .Where(authorization => authorization.ExpiresAt < limit)
                    .ExecuteDeleteAsync(stoppingToken);
                Deleted(logger, deleted);
            }
#pragma warning disable CA1031 // Uma falha (ex.: banco indisponível) não derruba o host; a próxima execução tenta de novo.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Failed(logger, exception);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Autorizações pendentes expiradas apagadas: {Count}.")]
    private static partial void Deleted(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Falha ao apagar autorizações pendentes expiradas.")]
    private static partial void Failed(ILogger logger, Exception exception);
}
