using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Vitrine.MercadoLivre.Infrastructure.Persistence;

/// <summary>
/// Job diário do módulo (contrato da SPEC-0020): mensagem recorrente do Wolverine (ADR-0014), agendada por
/// <see cref="MercadoLivreModule.CleanupSchedule"/> e tratada por <see cref="CleanupPendingAuthorizationsHandler"/>.
/// </summary>
public sealed record CleanupPendingAuthorizations;

/// <summary>Apaga as autorizações pendentes expiradas há mais de 1 dia (<c>expires_at &lt; agora - 1 dia</c>).</summary>
public static partial class CleanupPendingAuthorizationsHandler
{
    /// <summary>Quanto tempo uma autorização expirada continua guardada.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    public static async Task Handle(
        CleanupPendingAuthorizations message,
        MercadoLivreModuleDbContext db,
        TimeProvider clock,
        ILogger<CleanupPendingAuthorizations> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var limit = clock.GetUtcNow() - Retention;
        var deleted = await db.PendingAuthorizations
            .Where(authorization => authorization.ExpiresAt < limit)
            .ExecuteDeleteAsync(cancellationToken);
        Deleted(logger, deleted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Autorizações pendentes expiradas apagadas: {Count}.")]
    private static partial void Deleted(ILogger logger, int count);
}
