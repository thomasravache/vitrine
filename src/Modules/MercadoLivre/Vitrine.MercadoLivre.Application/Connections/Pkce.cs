using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Vitrine.MercadoLivre.Application.Connections;

/// <summary>
/// PKCE S256 (RFC 7636) e <c>state</c> do OAuth do Mercado Livre (SPEC-0020, ADR-0007).
/// </summary>
public static class Pkce
{
    /// <summary>Tamanho do <c>code_verifier</c> gerado (alfabeto <c>[A-Za-z0-9-._~]</c>).</summary>
    public const int VerifierLength = 64;

    /// <summary>Bytes aleatórios do <c>state</c> (codificado em base64url).</summary>
    public const int StateBytes = 32;

    // Alfabeto "unreserved" do RFC 7636 (seção 4.1).
    private const string VerifierAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>Gera um par verifier/challenge novo e um <c>state</c> novo.</summary>
    public static PkceSecrets Generate()
    {
        var verifier = RandomNumberGenerator.GetString(VerifierAlphabet, VerifierLength);
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateBytes));
        return new PkceSecrets(state, verifier, ChallengeFor(verifier));
    }

    /// <summary><c>code_challenge</c> = base64url(SHA-256(<paramref name="codeVerifier"/>)), sem padding.</summary>
    public static string ChallengeFor(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>Hash guardado da autorização pendente: SHA-256 do <c>state</c> em hexadecimal (o state nunca é gravado).</summary>
    public static string HashState(string state)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

}

/// <summary>Segredos de uma autorização: <c>state</c>, <c>code_verifier</c> e o <c>code_challenge</c> derivado.</summary>
public sealed record PkceSecrets(string State, string CodeVerifier, string CodeChallenge)
{
    /// <summary>Sem os valores: o verifier nunca vai para log.</summary>
    public override string ToString() => nameof(PkceSecrets);
}
