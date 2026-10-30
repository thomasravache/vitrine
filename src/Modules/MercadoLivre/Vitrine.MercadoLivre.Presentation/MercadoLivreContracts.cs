using System.Text.Json.Serialization;

namespace Vitrine.MercadoLivre.Presentation;

/// <summary>Resposta 200 de <c>POST /api/v1/mercadolivre/connections</c>: a página de autorização do ML (PKCE S256).</summary>
public sealed record StartConnectionResponse(string AuthorizationUrl);

/// <summary>
/// Corpo de <c>POST /api/v1/mercadolivre/connections/complete</c>: o que o ML devolveu na URL de retorno
/// (<c>code</c> 1..512, <c>state</c> 1..128; ausentes ou fora do tamanho: 400 <c>VALIDATION_FAILED</c>).
/// </summary>
public sealed record CompleteConnectionRequest(string Code, string State);

/// <summary>Resposta 201/200 de <c>POST /api/v1/mercadolivre/connections/complete</c> (<c>status</c>: <c>connected</c>).</summary>
/// <remarks><c>mlUserId</c> é int64 e sai sempre como número JSON (sem a leitura de string do padrão web).</remarks>
public sealed record ConnectedAccountResponse(
    Guid AccountId, [property: JsonNumberHandling(JsonNumberHandling.Strict)] long MlUserId, string Nickname, string Status);

/// <summary>Conta em <c>GET /api/v1/mercadolivre/accounts</c> (<c>status</c>: <c>connected</c> | <c>reauthorization_required</c>).</summary>
public sealed record MercadoLivreAccountResponse(
    Guid AccountId,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] long MlUserId,
    string Nickname,
    string SiteId,
    string Status,
    DateTimeOffset ConnectedAt);

/// <summary>Valores do campo <c>code</c> dos erros (application/problem+json) do módulo MercadoLivre.</summary>
public static class MercadoLivreErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string NotConfigured = "ML_NOT_CONFIGURED";
    public const string StateInvalid = "OAUTH_STATE_INVALID";
    public const string CodeInvalid = "OAUTH_CODE_INVALID";
    public const string Unavailable = "ML_UNAVAILABLE";
    public const string SiteNotSupported = "ML_SITE_NOT_SUPPORTED";
    public const string AccountInUse = "ML_ACCOUNT_IN_USE";
}
