using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Vitrine.MercadoLivre.Application.Notifications;

namespace Vitrine.MercadoLivre.Presentation;

/// <summary>
/// Webhook de notificações do ML (SPEC-0023, §6): <c>POST /api/v1/mercadolivre/notifications</c>, chamado pelo servidor
/// do ML. Anônimo (entrada da lista <c>AnonymousEndpoints</c>, emenda v5 da SPEC-0008), sem antiforgery nem sessão, fora
/// do documento OpenAPI (não é API da SPA), corpo de até <see cref="MaxBodyBytes"/> e rate limit por IP no host
/// (<see cref="EndpointName"/>). Nenhuma resposta reflete o corpo recebido.
/// </summary>
public static class NotificationsEndpoint
{
    /// <summary>Nome do endpoint (<c>WithName</c>): o host aplica o limite por IP do webhook por ele.</summary>
    public const string EndpointName = "ReceiveMercadoLivreNotification";

    /// <summary>Teto do corpo (contrato: 16 KB; acima, 413).</summary>
    public const int MaxBodyBytes = 16 * 1024;

    internal const string Pattern = "/notifications";

    internal static void Map(RouteGroupBuilder group) =>
        group.MapPost(Pattern, ReceiveAsync)
            .WithName(EndpointName)
            .AllowAnonymous()
            .ExcludeFromDescription();

    private static async Task<Results<Ok, ProblemHttpResult>> ReceiveAsync(
        HttpRequest request, ReceiveNotificationHandler handler, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes || await ReadCappedAsync(request.Body, cancellationToken) is not { } body)
        {
            return Problems.PayloadTooLarge();
        }

        return await handler.HandleAsync(body, cancellationToken) switch
        {
            ReceiveNotificationResult.Received => TypedResults.Ok(),
            ReceiveNotificationResult.Invalid invalid => Problems.ValidationFailed(invalid.Errors),
            _ => Problems.NotConfigured(),
        };
    }

    // Lê até MaxBodyBytes + 1 bytes (com ou sem Content-Length): além do teto devolve null sem consumir o resto.
    private static async Task<byte[]?> ReadCappedAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
            && (read = await body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total > MaxBodyBytes ? null : buffer[..total];
    }
}
