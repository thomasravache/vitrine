using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Vitrine.Common.Web;
using Vitrine.Listings.Application.Browsing;
using Vitrine.Listings.Application.Sync;

namespace Vitrine.Listings.Presentation;

/// <summary>Endpoints do módulo Listings (contrato da SPEC-0022).</summary>
public static class ListingsEndpoints
{
    /// <summary>
    /// Mapeia <c>/api/v1/listings</c>: listagem paginada, resumo e "sincronizar agora". Tudo exige sessão (política de
    /// fallback do host — ADR-0007) e qualquer membro do workspace; o POST exige HTTPS e antiforgery.
    /// </summary>
    public static IEndpointRouteBuilder MapListingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/listings").WithTags("Listings");
        group.MapGet(string.Empty, ListListings).WithName("ListListings")
            .AddOpenApiOperationTransformer(ListingsOpenApi.DocumentQueryTypes)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapGet("/summary", Summary).WithName("GetListingsSummary")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapPost("/sync", RequestSync).WithName("SyncListings")
            .AddEndpointFilter<HttpsRequiredFilter>()
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AddOpenApiOperationTransformer(ListingsOpenApi.OptionalRequestBody)
            .Finally(AcceptRequestsWithoutContentType);

        return endpoints;
    }

    private static async Task<Results<Ok<ListingsPageResponse>, UnauthorizedHttpResult, ProblemHttpResult>> ListListings(
        [FromQuery] string? status,
        [FromQuery] string? accountId,
        [FromQuery] string? q,
        [FromQuery] string? limit,
        [FromQuery] string? cursor,
        ClaimsPrincipal principal,
        ListListingsHandler handler,
        CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var request = new ListListingsQuery(status, accountId, q, limit, cursor);
        return await handler.HandleAsync(userId, request, cancellationToken) switch
        {
            ListListingsResult.Listed { Page: var page } => TypedResults.Ok(new ListingsPageResponse(
                [.. page.Items.Select(Response)], page.Next?.Encode())),
            ListListingsResult.Invalid invalid => Problems.ValidationFailed(invalid.Errors),
            _ => Problems.Forbidden(),
        };
    }

    private static async Task<Results<Ok<ListingsSummaryResponse>, UnauthorizedHttpResult, ProblemHttpResult>> Summary(
        ClaimsPrincipal principal, ListingsSummaryHandler handler, CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (await handler.HandleAsync(userId, cancellationToken) is not { } summary)
        {
            return Problems.Forbidden();
        }

        return TypedResults.Ok(new ListingsSummaryResponse(
            summary.Total,
            new ListingsByStatusResponse(summary.Active, summary.Paused, summary.Closed, summary.Other),
            [.. summary.Accounts.Select(account => new ListingsAccountSummaryResponse(
                account.AccountId, account.ItemCount, account.Status, account.CompletedAt, account.LastError))]));
    }

    // Corpo opcional (pode chegar nulo ou {}): sem accountId, todas as contas do workspace.
    private static async Task<Results<Accepted<SyncListingsResponse>, UnauthorizedHttpResult, ProblemHttpResult>> RequestSync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SyncListingsRequest request,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        RequestSyncHandler handler,
        CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        switch (await handler.HandleAsync(userId, request?.AccountId, cancellationToken))
        {
            case RequestSyncResult.Queued queued:
                return TypedResults.Accepted((string?)null, new SyncListingsResponse(queued.AccountIds));
            case RequestSyncResult.NotFound:
                return Problems.NotFound();
            case RequestSyncResult.ReauthorizationRequired:
                return Problems.ReauthorizationRequired();
            case RequestSyncResult.TooSoon tooSoon:
                httpContext.Response.Headers[HeaderNames.RetryAfter] = tooSoon.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return Problems.SyncTooSoon(tooSoon.RetryAfterSeconds);
            default:
                return Problems.Forbidden();
        }
    }

    /// <summary>
    /// Sem o metadado de Content-Type do corpo, o roteamento não descarta a rota quando a requisição chega sem corpo:
    /// ela continua sendo esta rota (401 da política de fallback, ou o pedido de todas as contas). O corpo JSON continua
    /// sendo lido e documentado.
    /// </summary>
    private static void AcceptRequestsWithoutContentType(EndpointBuilder endpoint)
    {
        foreach (var accepts in endpoint.Metadata.OfType<IAcceptsMetadata>().ToList())
        {
            endpoint.Metadata.Remove(accepts);
        }
    }

    private static Guid? UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;

    private static ListingResponse Response(ListingView listing) => new(
        listing.ItemId,
        listing.AccountId,
        listing.Title,
        listing.Price,
        listing.CurrencyId,
        listing.AvailableQuantity,
        listing.SoldQuantity,
        listing.Status,
        listing.Permalink,
        listing.ThumbnailUrl,
        listing.LastUpdated);
}
