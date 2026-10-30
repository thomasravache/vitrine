using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Respostas no formato do ML (snake_case), conforme as fixtures de <c>tests/Fixtures/MercadoLivre/</c>.</summary>
internal static class MlJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Data no formato do ML (<c>2026-10-28T18:42:07.000Z</c>).</summary>
    public static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

internal sealed record OAuthError(string Error, string ErrorDescription, int Status, IReadOnlyList<string> Cause);

internal sealed record ApiError(string Message, string Error, int Status, IReadOnlyList<string> Cause);

internal sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string Scope, long UserId, string RefreshToken);

internal sealed record UserResponse(
    long Id,
    string Nickname,
    string RegistrationDate,
    string CountryId,
    string UserType,
    IReadOnlyList<string> Tags,
    string SiteId,
    string Permalink);

internal sealed record OffsetPaging(int Limit, int Offset, int Total);

internal sealed record ScanPaging(int Limit, int Total);

internal sealed record SearchResponse(string SellerId, string? Query, OffsetPaging Paging, IReadOnlyList<string> Results);

internal sealed record ScanResponse(string SellerId, string? Query, ScanPaging Paging, IReadOnlyList<string> Results, string? ScrollId);

internal sealed record BulkEntry(string Id, int StatusCode, object Body);

/// <summary>Notificação do ML (<c>topic=items</c>) entregue ao webhook.</summary>
internal sealed record Notification(
    [property: JsonPropertyName("_id")] string Id,
    string Resource,
    long UserId,
    string Topic,
    long ApplicationId,
    int Attempts,
    string Sent,
    string Received,
    IReadOnlyList<string> Actions);
