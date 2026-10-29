namespace Vitrine.Common.Security;

/// <summary>
/// Cifra de campos sensíveis (contrato da SPEC-0008): AES-256-GCM via ASP.NET Core Data Protection, com um propósito
/// por campo. Texto adulterado ou propósito diferente lança <see cref="System.Security.Cryptography.CryptographicException"/>
/// — nunca devolve texto parcial.
/// </summary>
public interface IFieldProtector
{
    /// <summary>Cifra <paramref name="plaintext"/> para o campo identificado por <paramref name="purpose"/>.</summary>
    string Protect(string plaintext, string purpose);

    /// <summary>Decifra um valor produzido por <see cref="Protect"/> com o mesmo <paramref name="purpose"/>.</summary>
    string Unprotect(string ciphertext, string purpose);
}
