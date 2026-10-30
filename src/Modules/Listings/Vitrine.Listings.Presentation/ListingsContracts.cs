using System.Text.Json.Serialization;

namespace Vitrine.Listings.Presentation;

/// <summary>Anúncio em <c>GET /api/v1/listings</c> (contrato da SPEC-0022). Números saem sempre como número JSON.</summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ListingResponse(
    string ItemId,
    Guid AccountId,
    string Title,
    decimal Price,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    string? Permalink,
    string? ThumbnailUrl,
    DateTimeOffset LastUpdated);

/// <summary>Página de <c>GET /api/v1/listings</c>, ordenada por <c>lastUpdated desc, itemId</c>; <c>nextCursor</c> nulo na última.</summary>
public sealed record ListingsPageResponse(IReadOnlyList<ListingResponse> Items, string? NextCursor);

/// <summary>Resposta 200 de <c>GET /api/v1/listings/summary</c>.</summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ListingsSummaryResponse(int Total, ListingsByStatusResponse ByStatus, IReadOnlyList<ListingsAccountSummaryResponse> Accounts);

/// <summary>Anúncios por situação no ML (<c>other</c>: fora de active/paused/closed, ex.: <c>under_review</c>).</summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ListingsByStatusResponse(int Active, int Paused, int Closed, int Other);

/// <summary>
/// Estado da sincronização de uma conta (<c>syncStatus</c>: <c>idle</c> | <c>running</c> | <c>failed</c> | <c>paused</c>;
/// <c>lastError</c>: <c>ML_UNAVAILABLE</c> ou nulo; <c>lastSyncedAt</c>: conclusão da última sincronização bem-sucedida).
/// </summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ListingsAccountSummaryResponse(Guid AccountId, int ItemCount, string SyncStatus, DateTimeOffset? LastSyncedAt, string? LastError);

/// <summary>Corpo de <c>POST /api/v1/listings/sync</c>: <c>accountId</c> ausente = todas as contas do workspace.</summary>
public sealed record SyncListingsRequest
{
    /// <summary>Conta a sincronizar (opcional).</summary>
    public Guid? AccountId { get; init; }
}

/// <summary>Resposta 202 de <c>POST /api/v1/listings/sync</c>: contas atendidas (enfileiradas ou já sincronizando).</summary>
public sealed record SyncListingsResponse(IReadOnlyList<Guid> Queued);

/// <summary>Valores do campo <c>code</c> dos erros (application/problem+json) do módulo Listings.</summary>
public static class ListingsErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ReauthorizationRequired = "ACCOUNT_REAUTHORIZATION_REQUIRED";
    public const string SyncTooSoon = "SYNC_TOO_SOON";
}
