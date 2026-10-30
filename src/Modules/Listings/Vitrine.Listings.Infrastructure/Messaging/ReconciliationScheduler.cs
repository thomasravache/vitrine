using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vitrine.Listings.Application.Sync;
using Wolverine;

namespace Vitrine.Listings.Infrastructure.Messaging;

/// <summary>
/// Agendador leve da reconciliação (SPEC-0022 §4, ADR-0010): a cada <see cref="ListingsOptions.ReconciliationInterval"/>
/// (o primeiro disparo só depois de um intervalo) publica <see cref="ReconcileListings"/> na fila local durável; o
/// trabalho fica no handler durável (ADR-0014). Um intervalo arbitrário (inclusive de segundos, nos testes) não cabe numa
/// expressão cron, por isso não é uma mensagem recorrente por cron do Wolverine.
/// </summary>
internal sealed partial class ReconciliationScheduler(
    IServiceScopeFactory scopes, IOptions<ListingsOptions> options, TimeProvider clock, ILogger<ReconciliationScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ReconciliationInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PublishAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host parando.
        }
    }

    private async Task PublishAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new ReconcileListings());
        }
#pragma warning disable CA1031 // Uma falha ao publicar não derruba o agendador: o próximo intervalo tenta de novo.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            PublishFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Não foi possível publicar a reconciliação dos anúncios.")]
    private static partial void PublishFailed(ILogger logger, Exception exception);
}
