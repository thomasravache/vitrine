using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Corpo de <c>POST /__sim/sellers</c>.</summary>
internal sealed record CreateSellerRequest(string? Nickname, string? SiteId, int? Active, int? Paused, int? Closed);

/// <summary>Corpo de <c>POST /__sim/sellers/{id}/items</c>.</summary>
internal sealed record AddItemsRequest(int Count);

/// <summary>Corpo de <c>POST /__sim/sellers/{id}/items/{itemId}</c>.</summary>
internal sealed record ChangeItemRequest(string? Title, decimal? Price, int? AvailableQuantity, int? SoldQuantity, string? Status);

/// <summary>Corpo de <c>POST /__sim/notify</c>.</summary>
internal sealed record NotifyRequest(long UserId, string? ItemId);

/// <summary>
/// Endpoints de controle <c>/__sim/*</c> (só para dev/E2E; JSON camelCase): massa de dados, revogação, expiração do
/// access token, reset e disparo de notificações.
/// </summary>
internal static class ControlEndpoints
{
    /// <summary>Nome do <c>HttpClient</c> que entrega as notificações.</summary>
    public const string NotificationClient = "notifications";

    private const int DefaultActive = 15;
    private const int DefaultPaused = 6;
    private const int DefaultClosed = 4;
    private const int MaxItemsPerCall = 50_000;

    public static void MapControl(this IEndpointRouteBuilder app)
    {
        var control = app.MapGroup("/__sim");
        control.MapPost("/reset", Reset);
        control.MapPost("/sellers", CreateSeller);
        control.MapPost("/sellers/{sellerId:long}/items", AddItems);
        control.MapPost("/sellers/{sellerId:long}/items/{itemId}", ChangeItem);
        control.MapPost("/sellers/{sellerId:long}/revoke", Revoke);
        control.MapPost("/sellers/{sellerId:long}/expire-access-token", ExpireAccessToken);
        control.MapPost("/notify", NotifyAsync);
    }

    private static IResult Reset(SimulatorState state, TokenIssuer issuer)
    {
        state.Reset();
        issuer.Clear();
        return Results.NoContent();
    }

    private static IResult CreateSeller(CreateSellerRequest? request, SimulatorState state)
    {
        request ??= new CreateSellerRequest(null, null, null, null, null);
        if (Site.Find(request.SiteId ?? "MLB") is not { } site)
        {
            return Problem($"siteId desconhecido: {request.SiteId}.");
        }

        var (active, paused, closed) = (request.Active ?? DefaultActive, request.Paused ?? DefaultPaused, request.Closed ?? DefaultClosed);
        if (active < 0 || paused < 0 || closed < 0 || active + paused + closed > MaxItemsPerCall)
        {
            return Problem($"active, paused e closed precisam ser >= 0 e somar até {MaxItemsPerCall}.");
        }

        var seller = state.CreateSeller(request.Nickname, site, active, paused, closed);
        return Results.Created(
            string.Create(CultureInfo.InvariantCulture, $"/__sim/sellers/{seller.Id}"),
            new { id = seller.Id, nickname = seller.Nickname, siteId = seller.Site.Id });
    }

    private static IResult AddItems(long sellerId, AddItemsRequest request, SimulatorState state)
    {
        if (request.Count is < 1 or > MaxItemsPerCall)
        {
            return Problem($"count precisa estar entre 1 e {MaxItemsPerCall}.");
        }

        return state.AddItems(sellerId, request.Count) == ChangeResult.Changed
            ? Results.Ok(new { sellerId, added = request.Count, total = state.ItemIds(sellerId, null).Count })
            : NotFound($"Vendedor {sellerId} não existe.");
    }

    private static IResult ChangeItem(long sellerId, string itemId, ChangeItemRequest request, SimulatorState state) =>
        state.UpdateItem(sellerId, itemId, new ItemChange(request.Title, request.Price, request.AvailableQuantity, request.SoldQuantity, request.Status)) switch
        {
            ChangeResult.Changed => Results.Ok(state.FindItem(itemId)),
            ChangeResult.SellerNotFound => NotFound($"Vendedor {sellerId} não existe."),
            ChangeResult.ItemNotFound => NotFound($"Anúncio {itemId} não existe ou não é do vendedor {sellerId}."),
            _ => Problem("Campos inválidos: status active|paused|closed, números >= 0 e título não vazio."),
        };

    private static IResult Revoke(long sellerId, SimulatorState state, TokenIssuer issuer)
    {
        if (state.FindSeller(sellerId) is null)
        {
            return NotFound($"Vendedor {sellerId} não existe.");
        }

        issuer.Revoke(sellerId);
        return Results.NoContent();
    }

    private static IResult ExpireAccessToken(long sellerId, SimulatorState state, TokenIssuer issuer)
    {
        if (state.FindSeller(sellerId) is null)
        {
            return NotFound($"Vendedor {sellerId} não existe.");
        }

        issuer.ExpireAccessToken(sellerId);
        return Results.NoContent();
    }

    /// <summary>
    /// Envia ao <c>Simulator:NotificationUrl</c>, de forma síncrona, a notificação do ML (<c>topic=items</c>,
    /// <c>application_id</c> = client_id, <c>_id</c> novo) e devolve o status entregue pelo receptor.
    /// </summary>
    private static async Task<IResult> NotifyAsync(
        NotifyRequest request, IOptions<SimulatorOptions> options, IHttpClientFactory httpClientFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.ItemId))
        {
            return Problem("userId e itemId são obrigatórios.");
        }

        if (!Uri.TryCreate(settings.NotificationUrl, UriKind.Absolute, out var target))
        {
            return Results.Problem(
                "Simulator:NotificationUrl não está configurada.", statusCode: StatusCodes.Status409Conflict);
        }

        var now = MlJson.Date(timeProvider.GetUtcNow());
        var notification = new Notification(
            Guid.NewGuid().ToString(),
            $"/items/{request.ItemId}",
            request.UserId,
            "items",
            long.Parse(settings.ClientId, CultureInfo.InvariantCulture),
            1,
            now,
            now,
            []);

        try
        {
            using var response = await httpClientFactory.CreateClient(NotificationClient)
                .PostAsJsonAsync(target, notification, MlJson.Options, cancellationToken);
            return Results.Json(
                new { notificationId = notification.Id, deliveredStatus = (int)response.StatusCode },
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (HttpRequestException exception)
        {
            return Results.Json(
                new { notificationId = notification.Id, deliveredStatus = (int?)null, error = exception.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult Problem(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status404NotFound);
}
