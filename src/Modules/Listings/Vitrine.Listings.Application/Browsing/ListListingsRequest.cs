using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vitrine.Listings.Application.Browsing;

/// <summary>Parâmetros crus de <c>GET /api/v1/listings</c> (contrato da SPEC-0022).</summary>
/// <param name="Status"><c>active</c> | <c>paused</c> | <c>closed</c> | <c>all</c>; ausente = <c>active</c>.</param>
/// <param name="AccountId">Filtro por conta (opcional).</param>
/// <param name="Q">Trecho do título (sem diferenciar maiúsculas) ou código exato <c>MLB\d+</c>; até 100 caracteres.</param>
/// <param name="Limit">Tamanho da página, 1..50; ausente = 20.</param>
/// <param name="Cursor">Cursor opaco de <see cref="ListingsCursor.Encode"/> (opcional).</param>
public sealed record ListListingsRequest(string? Status, Guid? AccountId, string? Q, int? Limit, string? Cursor);

/// <summary>Validação dos parâmetros da listagem (400 <c>VALIDATION_FAILED</c>).</summary>
public static class ListListingsValidator
{
    public const int DefaultLimit = 20;
    public const int MinLimit = 1;
    public const int MaxLimit = 50;
    public const int MaxQueryLength = 100;

    /// <summary>Filtro <c>status</c> aceito.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["active", "paused", "closed", "all"];

    /// <summary>
    /// Erros por campo (<c>status</c>, <c>limit</c>, <c>q</c>, <c>cursor</c>); vazio quando os parâmetros são válidos.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(ListListingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Status is { } status && !Statuses.Contains(status, StringComparer.Ordinal))
        {
            errors["status"] = [$"Use um destes valores: {string.Join(", ", Statuses)}."];
        }

        if (request.Limit is { } limit && (limit < MinLimit || limit > MaxLimit))
        {
            errors["limit"] = [$"O limite deve estar entre {MinLimit} e {MaxLimit}."];
        }

        if (request.Q is { Length: > MaxQueryLength })
        {
            errors["q"] = [$"A busca aceita até {MaxQueryLength} caracteres."];
        }

        if (!string.IsNullOrEmpty(request.Cursor) && !ListingsCursor.TryDecode(request.Cursor, out _))
        {
            errors["cursor"] = ["Cursor inválido."];
        }

        return errors;
    }
}

/// <summary>Consulta da listagem já validada e convertida.</summary>
/// <param name="Status"><c>active</c> | <c>paused</c> | <c>closed</c> | <c>all</c>.</param>
/// <param name="AccountId">Filtro por conta.</param>
/// <param name="TitleContains">Trecho do título (quando <c>q</c> não é um código).</param>
/// <param name="ItemId">Código exato (quando <c>q</c> casa <c>^MLB\d+$</c>).</param>
/// <param name="Limit">Tamanho da página.</param>
/// <param name="After">Posição depois da qual a página começa (keyset), ou nula na primeira página.</param>
public sealed partial record ListingsFilter(string Status, Guid? AccountId, string? TitleContains, string? ItemId, int Limit, ListingsCursor? After)
{
    /// <summary>Status que inclui todos os outros.</summary>
    public const string AllStatuses = "all";

    /// <summary>Converte parâmetros válidos (sem erros em <see cref="ListListingsValidator.Validate"/>).</summary>
    public static ListingsFilter From(ListListingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var q = string.IsNullOrWhiteSpace(request.Q) ? null : request.Q;
        var isCode = q is not null && ItemCode().IsMatch(q);
        ListingsCursor? after = null;
        if (!string.IsNullOrEmpty(request.Cursor) && !ListingsCursor.TryDecode(request.Cursor, out after))
        {
            throw new ArgumentException("Cursor inválido: valide com ListListingsValidator antes de converter.", nameof(request));
        }

        return new ListingsFilter(
            request.Status ?? "active",
            request.AccountId,
            isCode ? null : q,
            isCode ? q : null,
            request.Limit ?? ListListingsValidator.DefaultLimit,
            after);
    }

    [GeneratedRegex("^MLB[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemCode();
}

/// <summary>
/// Cursor keyset da listagem (<c>ml_last_updated desc, item_id</c>): a posição do último item da página. Codificado como
/// texto opaco (base64url de versão, instante em ticks UTC, código e uma soma de verificação); texto adulterado não
/// decodifica.
/// </summary>
/// <param name="LastUpdated">Última alteração no ML do último item da página.</param>
/// <param name="ItemId">Código do último item da página.</param>
public sealed record ListingsCursor(DateTimeOffset LastUpdated, string ItemId)
{
    private const byte Version = 1;
    private const int TicksLength = sizeof(long);
    private const int ChecksumLength = 8;
    private const int MaxItemIdLength = 32;

    /// <summary>Texto opaco do cursor (seguro em query string).</summary>
    public string Encode()
    {
        var itemId = Encoding.UTF8.GetBytes(ItemId);
        var payload = new byte[1 + TicksLength + itemId.Length + ChecksumLength];
        payload[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, TicksLength), LastUpdated.UtcTicks);
        itemId.CopyTo(payload.AsSpan(1 + TicksLength));
        Checksum(payload.AsSpan(0, payload.Length - ChecksumLength), payload.AsSpan(payload.Length - ChecksumLength));
        return Base64Url.EncodeToString(payload);
    }

    /// <summary>Decodifica um texto de <see cref="Encode"/>; falso para texto inválido ou adulterado.</summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out ListingsCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(text) || text.Length > 128 || !Base64Url.IsValid(text, out var length))
        {
            return false;
        }

        var payload = new byte[length];
        if (!Base64Url.TryDecodeFromChars(text, payload, out var written) || written != length)
        {
            return false;
        }

        var itemIdLength = length - 1 - TicksLength - ChecksumLength;
        if (itemIdLength is < 1 or > MaxItemIdLength || payload[0] != Version)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[ChecksumLength];
        Checksum(payload.AsSpan(0, length - ChecksumLength), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, payload.AsSpan(length - ChecksumLength)))
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1, TicksLength));
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        string itemId;
        try
        {
            itemId = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload, 1 + TicksLength, itemIdLength);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var decoded = new ListingsCursor(new DateTimeOffset(ticks, TimeSpan.Zero), itemId);

        // Só a forma canônica: qualquer outra grafia do mesmo conteúdo também é recusada.
        if (!string.Equals(decoded.Encode(), text, StringComparison.Ordinal))
        {
            return false;
        }

        cursor = decoded;
        return true;
    }

    private static void Checksum(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(data, hash);
        hash[..ChecksumLength].CopyTo(destination);
    }
}
