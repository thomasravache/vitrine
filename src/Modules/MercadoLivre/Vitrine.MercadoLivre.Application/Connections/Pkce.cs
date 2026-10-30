namespace Vitrine.MercadoLivre.Application.Connections;

/// <summary>
/// PKCE S256 (RFC 7636) e <c>state</c> do OAuth do Mercado Livre (SPEC-0020, ADR-0007). Scaffold do contrato.
/// </summary>
public static class Pkce
{
    /// <summary>Tamanho do <c>code_verifier</c> gerado (alfabeto <c>[A-Za-z0-9-._~]</c>).</summary>
    public const int VerifierLength = 64;

    /// <summary>Bytes aleatórios do <c>state</c> (codificado em base64url).</summary>
    public const int StateBytes = 32;

    /// <summary>Gera um par verifier/challenge novo e um <c>state</c> novo.</summary>
    public static PkceSecrets Generate() =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");

    /// <summary><c>code_challenge</c> = base64url(SHA-256(<paramref name="codeVerifier"/>)), sem padding.</summary>
    public static string ChallengeFor(string codeVerifier) =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");
}

/// <summary>Segredos de uma autorização: <c>state</c>, <c>code_verifier</c> e o <c>code_challenge</c> derivado.</summary>
public sealed record PkceSecrets(string State, string CodeVerifier, string CodeChallenge)
{
    /// <summary>Sem os valores: o verifier nunca vai para log.</summary>
    public override string ToString() => nameof(PkceSecrets);
}
