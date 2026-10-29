using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Vitrine.Api.Observability;

/// <summary>
/// Readiness do PostgreSQL (<c>/health/ready</c>): abre uma conexão do pool e roda <c>SELECT 1</c>. O resultado sai
/// só como estado agregado no endpoint; a descrição e a exceção ficam nos logs internos.
/// </summary>
internal sealed class PostgresReadinessCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("vitrine");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("ConnectionStrings:vitrine não configurada.");
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL inacessível.", exception);
        }
    }
}
