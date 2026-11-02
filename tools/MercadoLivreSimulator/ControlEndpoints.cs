using System.Globalization;
using System.Text.Json.Serialization;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Corpo de <c>POST /__sim/sellers</c>.</summary>
internal sealed record CreateSellerRequest(string? Nickname, string? SiteId, int? Active, int? Paused, int? Closed);

/// <summary>Corpo de <c>POST /__sim/sellers/{id}/items</c>.</summary>
internal sealed record AddItemsRequest(int Count);

/// <summary>
/// Corpo de <c>POST /__sim/sellers/{id}/items/{itemId}</c>: <c>description</c> vazia apaga a descrição;
/// <c>attributes</c> substitui a lista (SPEC-0034).
/// </summary>
internal sealed record ChangeItemRequest(
    string? Title,
    decimal? Price,
    int? AvailableQuantity,
    int? SoldQuantity,
    string? Status,
    bool? CatalogListing,
    string? Description,
    IReadOnlyList<ControlAttributeRequest>? Attributes);

/// <summary>Atributo no controle, com <c>value_id</c>/<c>value_name</c> em snake_case como no ML.</summary>
internal sealed record ControlAttributeRequest(
    string Id,
    [property: JsonPropertyName("value_id")] string? ValueId,
    [property: JsonPropertyName("value_name")] string? ValueName);

/// <summary>Corpo de <c>POST /__sim/sellers/{id}/items/{itemId}/fail-next-write</c>.</summary>
internal sealed record FailNextWriteRequest(string? Target, int? Status, string? Error, string? Message, string? CauseCode, int? Times);

/// <summary>Corpo de <c>POST /__sim/notify</c>.</summary>
internal sealed record NotifyRequest(long UserId, string? ItemId);

/// <summary>
/// Endpoints de controle <c>/__sim/*</c> (só para dev/E2E; JSON camelCase): massa de dados, revogação, expiração do
/// access token, reset e disparo de notificações.
/// </summary>
internal static class ControlEndpoints
{
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
        control.MapPost("/sellers/{sellerId:long}/items/{itemId}/fail-next-write", FailNextWrite);
        control.MapGet("/sellers/{sellerId:long}/items/{itemId}/writes", Writes);
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

    private static IResult ChangeItem(long sellerId, string itemId, ChangeItemRequest request, SimulatorState state)
    {
        var change = new ItemChange(request.Title, request.Price, request.AvailableQuantity, request.SoldQuantity, request.Status)
        {
            CatalogListing = request.CatalogListing,
            Description = request.Description,
            Attributes = request.Attributes?.Select(attribute => new AttributeUpdate(attribute.Id, attribute.ValueId, attribute.ValueName)).ToList(),
        };
        return state.UpdateItem(sellerId, itemId, change) switch
        {
            ChangeResult.Changed => Results.Ok(state.FindItem(itemId)),
            ChangeResult.SellerNotFound => NotFound($"Vendedor {sellerId} não existe."),
            ChangeResult.ItemNotFound => NotFound(ItemNotFound(sellerId, itemId)),
            _ => Problem("Campos inválidos: status active|paused|closed, números >= 0 e título não vazio."),
        };
    }

    /// <summary>
    /// Força a resposta das próximas <c>times</c> escritas (padrão 1) no alvo <c>item</c> ou <c>description</c>, no
    /// formato de erro do ML, sem mudar nada no anúncio.
    /// </summary>
    private static IResult FailNextWrite(long sellerId, string itemId, FailNextWriteRequest request, SimulatorState state)
    {
        WriteTarget? target = request.Target switch
        {
            "item" => WriteTarget.Item,
            "description" => WriteTarget.Description,
            _ => null,
        };
        if (target is null || request.Status is not (400 or 409 or 500) || request.Times is < 1)
        {
            return Problem("Use target item|description, status 400|409|500 e times >= 1.");
        }

        var status = request.Status.Value;
        var (message, error) = status switch
        {
            StatusCodes.Status409Conflict => ("Optimistic locking error, please try again in a few seconds.", "conflict"),
            StatusCodes.Status500InternalServerError => ("Internal server error", "internal_error"),
            _ => ("Validation error", "validation_error"),
        };
        message = request.Message ?? message;
        IReadOnlyList<ApiCause> cause = string.IsNullOrWhiteSpace(request.CauseCode) ? [] : [new ApiCause(request.CauseCode, message)];
        var failure = new PendingWriteFailure(target.Value, new ApiError(message, request.Error ?? error, status, cause), request.Times ?? 1);
        return state.FailNextWrite(sellerId, itemId, failure) switch
        {
            ChangeResult.Changed => Results.NoContent(),
            ChangeResult.SellerNotFound => NotFound($"Vendedor {sellerId} não existe."),
            _ => NotFound(ItemNotFound(sellerId, itemId)),
        };
    }

    /// <summary>Escritas recebidas pela API do ML para o anúncio: <c>[{ method, path, body, status, at }]</c>.</summary>
    private static IResult Writes(long sellerId, string itemId, SimulatorState state) =>
        state.Writes(sellerId, itemId) is { } writes ? Results.Ok(writes) : NotFound(ItemNotFound(sellerId, itemId));

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
    private static async Task<IResult> NotifyAsync(NotifyRequest request, NotificationSender sender, CancellationToken cancellationToken)
    {
        if (request.UserId <= 0 || string.IsNullOrWhiteSpace(request.ItemId))
        {
            return Problem("userId e itemId são obrigatórios.");
        }

        if (await sender.SendAsync(request.UserId, request.ItemId, cancellationToken) is not { } delivery)
        {
            return Results.Problem(
                "Simulator:NotificationUrl não está configurada.", statusCode: StatusCodes.Status409Conflict);
        }

        return delivery.DeliveredStatus is { } delivered
            ? Results.Json(
                new { notificationId = delivery.NotificationId, deliveredStatus = delivered },
                statusCode: StatusCodes.Status202Accepted)
            : Results.Json(
                new { notificationId = delivery.NotificationId, deliveredStatus = (int?)null, error = delivery.Error },
                statusCode: StatusCodes.Status502BadGateway);
    }

    private static string ItemNotFound(long sellerId, string itemId) => $"Anúncio {itemId} não existe ou não é do vendedor {sellerId}.";

    private static IResult Problem(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status404NotFound);
}
