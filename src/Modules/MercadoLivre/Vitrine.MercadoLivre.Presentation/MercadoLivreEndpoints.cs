using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Vitrine.Common.Web;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Application.Accounts;
using Vitrine.MercadoLivre.Application.Connections;
using Vitrine.MercadoLivre.Domain.Accounts;

namespace Vitrine.MercadoLivre.Presentation;

/// <summary>Endpoints do módulo MercadoLivre (contrato da SPEC-0020).</summary>
public static class MercadoLivreEndpoints
{
    private const string AccountsPath = "/api/v1/mercadolivre/accounts";

    /// <summary>
    /// Mapeia <c>/api/v1/mercadolivre</c>: iniciar e concluir a conexão, listar e desconectar contas. Tudo exige sessão
    /// (política de fallback do host — ADR-0007); POST e DELETE exigem HTTPS e antiforgery.
    /// </summary>
    public static IEndpointRouteBuilder MapMercadoLivreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/mercadolivre").WithTags("MercadoLivre");
        group.MapPost("/connections", StartConnection).WithName("StartMercadoLivreConnection")
            .AddEndpointFilter<HttpsRequiredFilter>()
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/connections/complete", CompleteConnection).WithName("CompleteMercadoLivreConnection")
            .AddEndpointFilter<HttpsRequiredFilter>()
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Finally(AcceptRequestsWithoutContentType);
        group.MapGet("/accounts", ListAccounts).WithName("ListMercadoLivreAccounts")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapDelete("/accounts/{accountId:guid}", DisconnectAccount).WithName("DisconnectMercadoLivreAccount")
            .AddEndpointFilter<HttpsRequiredFilter>()
            .AddEndpointFilter<AntiforgeryFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<Results<Ok<StartConnectionResponse>, UnauthorizedHttpResult, ProblemHttpResult>> StartConnection(
        ClaimsPrincipal principal, StartConnectionHandler handler, CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return await handler.HandleAsync(userId, cancellationToken) switch
        {
            StartConnectionResult.Started started => TypedResults.Ok(new StartConnectionResponse(started.AuthorizationUrl.AbsoluteUri)),
            StartConnectionResult.NotConfigured => Problems.NotConfigured(),
            _ => Problems.Forbidden(),
        };
    }

    // Corpo opcional no binding (pode chegar nulo): sem corpo a conclusão responde 400 VALIDATION_FAILED.
    private static async Task<Results<Created<ConnectedAccountResponse>, Ok<ConnectedAccountResponse>, UnauthorizedHttpResult, ProblemHttpResult>> CompleteConnection(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CompleteConnectionRequest request, ClaimsPrincipal principal, CompleteConnectionHandler handler, CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return await handler.HandleAsync(userId, request?.Code, request?.State, cancellationToken) switch
        {
            CompleteConnectionResult.Connected { Reconnected: true } connected => TypedResults.Ok(Response(connected)),
            CompleteConnectionResult.Connected connected => TypedResults.Created(AccountsPath, Response(connected)),
            CompleteConnectionResult.NotConfigured => Problems.NotConfigured(),
            CompleteConnectionResult.Invalid invalid => Problems.ValidationFailed(invalid.Errors),
            CompleteConnectionResult.StateInvalid => Problems.StateInvalid(),
            CompleteConnectionResult.CodeInvalid => Problems.CodeInvalid(),
            CompleteConnectionResult.SiteNotSupported => Problems.SiteNotSupported(),
            CompleteConnectionResult.AccountInUse => Problems.AccountInUse(),
            CompleteConnectionResult.MlUnavailable => Problems.Unavailable(),
            _ => Problems.Forbidden(),
        };
    }

    private static async Task<Results<Ok<List<MercadoLivreAccountResponse>>, UnauthorizedHttpResult, ProblemHttpResult>> ListAccounts(
        ClaimsPrincipal principal, ListAccountsHandler handler, CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        if (await handler.HandleAsync(userId, cancellationToken) is not { } accounts)
        {
            return Problems.Forbidden();
        }

        return TypedResults.Ok(accounts.Select(Response).ToList());
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, ProblemHttpResult>> DisconnectAccount(
        Guid accountId, ClaimsPrincipal principal, DisconnectAccountHandler handler, CancellationToken cancellationToken)
    {
        if (UserId(principal) is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        return await handler.HandleAsync(userId, accountId, cancellationToken) switch
        {
            DisconnectAccountResult.Disconnected => TypedResults.NoContent(),
            DisconnectAccountResult.NotFound => Problems.NotFound(),
            _ => Problems.Forbidden(),
        };
    }

    /// <summary>
    /// Sem o metadado de Content-Type do corpo, o roteamento não descarta a rota quando a requisição chega sem corpo (ex.:
    /// sem sessão): ela continua sendo esta rota — 401 da política de fallback, ou 400 VALIDATION_FAILED com sessão — em
    /// vez de cair no 404 de <c>/api/{**path}</c>. O corpo JSON continua sendo lido e documentado.
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

    private static ConnectedAccountResponse Response(CompleteConnectionResult.Connected connected) =>
        new(connected.AccountId, connected.MlUserId, connected.Nickname, StatusName(MercadoLivreAccountStatus.Connected));

    private static MercadoLivreAccountResponse Response(MercadoLivreAccountView account) =>
        new(account.AccountId, account.MlUserId, account.Nickname, account.SiteId, StatusName(account.Status), account.ConnectedAt);

    private static string StatusName(MercadoLivreAccountStatus status) => status switch
    {
        MercadoLivreAccountStatus.ReauthorizationRequired => "reauthorization_required",
        _ => "connected",
    };
}
