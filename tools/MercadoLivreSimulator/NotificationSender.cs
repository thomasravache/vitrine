using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Entrega de uma notificação: <c>_id</c> e o status do receptor (nulo quando não chegou).</summary>
internal sealed record NotificationDelivery(string NotificationId, int? DeliveredStatus, string? Error);

/// <summary>
/// Envio da notificação do ML (<c>topic=items</c>, <c>application_id</c> = client_id, <c>_id</c> novo) para
/// <c>Simulator:NotificationUrl</c>: síncrono no <c>POST /__sim/notify</c> e sem esperar a entrega depois de uma escrita
/// aceita (SPEC-0034).
/// </summary>
internal sealed partial class NotificationSender(
    IOptions<SimulatorOptions> options, IHttpClientFactory httpClientFactory, TimeProvider timeProvider, ILogger<NotificationSender> logger)
{
    /// <summary>Nome do <c>HttpClient</c> que entrega as notificações.</summary>
    public const string ClientName = "notifications";

    /// <summary>Destino configurado; <c>null</c> quando <c>Simulator:NotificationUrl</c> não está configurada.</summary>
    public Uri? Target => Uri.TryCreate(options.Value.NotificationUrl, UriKind.Absolute, out var target) ? target : null;

    /// <summary>Envia a notificação do anúncio e devolve a entrega (<c>null</c> sem destino configurado).</summary>
    public async Task<NotificationDelivery?> SendAsync(long userId, string itemId, CancellationToken cancellationToken)
    {
        if (Target is not { } target)
        {
            return null;
        }

        var now = MlJson.Date(timeProvider.GetUtcNow());
        var notification = new Notification(
            Guid.NewGuid().ToString(),
            $"/items/{itemId}",
            userId,
            "items",
            long.Parse(options.Value.ClientId, CultureInfo.InvariantCulture),
            1,
            now,
            now,
            []);

        try
        {
            using var response = await httpClientFactory.CreateClient(ClientName)
                .PostAsJsonAsync(target, notification, MlJson.Options, cancellationToken);
            return new NotificationDelivery(notification.Id, (int)response.StatusCode, null);
        }
        catch (HttpRequestException exception)
        {
            return new NotificationDelivery(notification.Id, null, exception.Message);
        }
    }

    /// <summary>Dispara a notificação depois de uma escrita aceita, sem bloquear a resposta (falha só gera log).</summary>
    public void Publish(long userId, string itemId)
    {
        if (Target is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var delivery = await SendAsync(userId, itemId, CancellationToken.None);
                if (delivery?.DeliveredStatus is not (>= 200 and < 300))
                {
                    LogNotDelivered(itemId, delivery?.DeliveredStatus, delivery?.Error);
                }
            }
            catch (Exception exception) when (exception is TaskCanceledException or InvalidOperationException)
            {
                LogNotDelivered(itemId, null, exception.Message);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notificação items de {ItemId} não entregue (status {Status}): {Error}")]
    private partial void LogNotDelivered(string itemId, int? status, string? error);
}
