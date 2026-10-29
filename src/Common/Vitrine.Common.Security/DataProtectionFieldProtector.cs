using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Vitrine.Common.Security;

/// <summary>
/// <see cref="IFieldProtector"/> sobre um <see cref="IDataProtectionProvider"/> (SPEC-0008): um protetor por propósito
/// (<c>Vitrine.FieldProtection</c> + propósito do campo), com o algoritmo configurado no host (AES-256-GCM — ADR-0006).
/// O valor protegido é Base64Url. Qualquer falha ao decifrar (texto adulterado, truncado, que não é um valor protegido
/// ou de outro propósito) vira <see cref="CryptographicException"/> sem texto parcial nem o valor na mensagem.
/// </summary>
public sealed class DataProtectionFieldProtector : IFieldProtector
{
    /// <summary>Propósito raiz: isola a cifra de campos dos demais usos do Data Protection (cookies, antiforgery).</summary>
    public const string RootPurpose = "Vitrine.FieldProtection";

    private readonly IDataProtector _root;
    private readonly ConcurrentDictionary<string, IDataProtector> _protectors = new(StringComparer.Ordinal);

    public DataProtectionFieldProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _root = provider.CreateProtector(RootPurpose);
    }

    public string Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return For(purpose).Protect(plaintext);
    }

    public string Unprotect(string ciphertext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        var protector = For(purpose);
        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or DecoderFallbackException)
        {
            // O Data Protection já homogeneíza quase tudo em CryptographicException; o resto também vira uma.
            throw new CryptographicException("O valor protegido é inválido ou foi alterado.", exception);
        }
    }

    private IDataProtector For(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return _protectors.GetOrAdd(purpose, static (key, root) => root.CreateProtector(key), _root);
    }
}
