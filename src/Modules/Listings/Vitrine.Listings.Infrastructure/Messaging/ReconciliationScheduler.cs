using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vitrine.Listings.Application.Sync;
using Wolverine;

namespace Vitrine.Listings.Infrastructure.Messaging;

/// <summary>
/// Agendador leve da reconciliação (SPEC-0022 §4, emenda v1.3): publica <see cref="ReconcileListings"/> na fila local
/// durável logo que o host termina de subir e depois a cada <see cref="Period"/> (<c>min(ReconciliationInterval, 1 h)</c>).
/// Quais contas sincronizar é decidido pelo handler a partir do estado persistido, então a cadência real não depende do
/// tempo no ar do processo (deploys frequentes) e publicações repetidas não duplicam trabalho. Um intervalo arbitrário
/// (inclusive de segundos, nos testes) não cabe numa expressão cron, por isso não é uma mensagem recorrente por cron.
/// </summary>
internal sealed partial class ReconciliationScheduler(
    IServiceScopeFactory scopes,
    IOptions<ListingsOptions> options,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<ReconciliationScheduler> logger)
    : BackgroundService
{
    /// <summary>Teto do período do timer: a conta vencida espera no máximo isto depois do vencimento.</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromHours(1);

    private TimeSpan Period =>
        options.Value.ReconciliationInterval < MaxPeriod ? options.Value.ReconciliationInterval : MaxPeriod;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Primeiro disparo assim que todos os serviços hospedados (inclusive o Wolverine) subiram.
            await WhenStartedAsync(stoppingToken);
            await PublishAsync();

            using var timer = new PeriodicTimer(Period, clock);
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

    private async Task WhenStartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            await started.Task.WaitAsync(stoppingToken);
        }
    }

    private async Task PublishAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new ReconcileListings());
        }
#pragma warning disable CA1031 // Uma falha ao publicar não derruba o agendador: o próximo disparo tenta de novo.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            PublishFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Não foi possível publicar a reconciliação dos anúncios.")]
    private static partial void PublishFailed(ILogger logger, Exception exception);
}
