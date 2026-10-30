using System.Globalization;
using System.Security.Cryptography;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Tokens emitidos para um vendedor (<c>expires_in</c> em segundos).</summary>
public sealed record IssuedTokens(long SellerId, string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Emissor de tokens do simulador: refresh token de uso único e rotacionado (só o último emitido vale), revogação e
/// expiração forçada do access token por vendedor.
/// </summary>
/// <remarks>Um par vigente por vendedor: emitir ou renovar substitui o par anterior.</remarks>
public sealed class TokenIssuer
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _accessTokenLifetime;
    private readonly Dictionary<long, Grant> _grants = [];
    private readonly Dictionary<string, long> _accessTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _refreshTokens = new(StringComparer.Ordinal);

    /// <summary>Emissor com o relógio <paramref name="timeProvider"/> e a validade <paramref name="accessTokenLifetime"/>.</summary>
    public TokenIssuer(TimeProvider timeProvider, TimeSpan accessTokenLifetime)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(accessTokenLifetime, TimeSpan.Zero);
        _timeProvider = timeProvider;
        _accessTokenLifetime = accessTokenLifetime;
    }

    /// <summary>Emite um par novo de tokens para o vendedor.</summary>
    public IssuedTokens Issue(long sellerId)
    {
        lock (_gate)
        {
            return IssueLocked(sellerId);
        }
    }

    /// <summary>Renova com o último refresh token emitido e não revogado; senão <c>null</c> (<c>invalid_grant</c>).</summary>
    public IssuedTokens? Refresh(string refreshToken)
    {
        lock (_gate)
        {
            return refreshToken is not null && _refreshTokens.TryGetValue(refreshToken, out var sellerId)
                ? IssueLocked(sellerId)
                : null;
        }
    }

    /// <summary>Vendedor dono do access token válido; <c>null</c> se desconhecido, expirado ou revogado (401).</summary>
    public long? ValidateAccessToken(string accessToken)
    {
        lock (_gate)
        {
            return accessToken is not null
                && _accessTokens.TryGetValue(accessToken, out var sellerId)
                && _grants.TryGetValue(sellerId, out var grant)
                && _timeProvider.GetUtcNow() < grant.AccessExpiresAt
                    ? sellerId
                    : null;
        }
    }

    /// <summary>Invalida access e refresh tokens do vendedor.</summary>
    public void Revoke(long sellerId)
    {
        lock (_gate)
        {
            if (_grants.Remove(sellerId, out var grant))
            {
                RemoveAccess(grant);
                _refreshTokens.Remove(grant.RefreshToken);
            }
        }
    }

    /// <summary>Invalida só o access token atual do vendedor (o refresh continua valendo).</summary>
    public void ExpireAccessToken(long sellerId)
    {
        lock (_gate)
        {
            if (_grants.TryGetValue(sellerId, out var grant))
            {
                RemoveAccess(grant);
                _grants[sellerId] = grant with { AccessToken = null };
            }
        }
    }

    /// <summary>Invalida todos os tokens de todos os vendedores (<c>POST /__sim/reset</c>).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _grants.Clear();
            _accessTokens.Clear();
            _refreshTokens.Clear();
        }
    }

    private IssuedTokens IssueLocked(long sellerId)
    {
        if (_grants.Remove(sellerId, out var previous))
        {
            RemoveAccess(previous);
            _refreshTokens.Remove(previous.RefreshToken);
        }

        var suffix = sellerId.ToString(CultureInfo.InvariantCulture);
        var accessToken = $"APP_USR-{RandomHex(16)}-{RandomHex(32)}-{suffix}";
        var refreshToken = $"TG-{RandomHex(24)}-{suffix}";
        _grants[sellerId] = new Grant(accessToken, _timeProvider.GetUtcNow() + _accessTokenLifetime, refreshToken);
        _accessTokens[accessToken] = sellerId;
        _refreshTokens[refreshToken] = sellerId;
        return new IssuedTokens(sellerId, accessToken, refreshToken, (int)_accessTokenLifetime.TotalSeconds);
    }

    private void RemoveAccess(Grant grant)
    {
        if (grant.AccessToken is not null)
        {
            _accessTokens.Remove(grant.AccessToken);
        }
    }

    private static string RandomHex(int length) => RandomNumberGenerator.GetHexString(length, lowercase: true);

    private sealed record Grant(string? AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken);
}
