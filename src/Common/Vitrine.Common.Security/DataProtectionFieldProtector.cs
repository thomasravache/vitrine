using Microsoft.AspNetCore.DataProtection;

namespace Vitrine.Common.Security;

/// <summary><see cref="IFieldProtector"/> sobre um <see cref="IDataProtectionProvider"/> (SPEC-0008).</summary>
public sealed class DataProtectionFieldProtector : IFieldProtector
{
    public DataProtectionFieldProtector(IDataProtectionProvider provider) => ArgumentNullException.ThrowIfNull(provider);

    public string Protect(string plaintext, string purpose) => throw new NotImplementedException();

    public string Unprotect(string ciphertext, string purpose) => throw new NotImplementedException();
}
