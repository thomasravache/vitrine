using System.Security.Cryptography;
using System.Text;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>Verificação PKCE S256 (RFC 7636): <c>BASE64URL(SHA256(code_verifier)) == code_challenge</c>.</summary>
public static class Pkce
{
    /// <summary>Verdadeiro quando o <paramref name="codeVerifier"/> corresponde ao <paramref name="codeChallenge"/> (S256).</summary>
    public static bool Verify(string codeVerifier, string codeChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
        {
            return false;
        }

        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(codeChallenge));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
