namespace Vitrine.MercadoLivreSimulator;

/// <summary>Verificação PKCE S256 (RFC 7636): <c>BASE64URL(SHA256(code_verifier)) == code_challenge</c>.</summary>
public static class Pkce
{
    /// <summary>Verdadeiro quando o <paramref name="codeVerifier"/> corresponde ao <paramref name="codeChallenge"/> (S256).</summary>
    public static bool Verify(string codeVerifier, string codeChallenge) => throw new NotImplementedException();
}
