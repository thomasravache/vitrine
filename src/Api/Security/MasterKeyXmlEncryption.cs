using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Vitrine.Api.Security;

/// <summary>
/// Cifra cada chave do anel do Data Protection com a chave mestra (AES-256-GCM, nonce aleatório) antes de gravá-la
/// no PostgreSQL: o banco nunca guarda chave em claro (ADR-0006).
/// </summary>
internal sealed class MasterKeyXmlEncryptor(DataProtectionMasterKey masterKey) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            var payload = new byte[MasterKeyXml.NonceSize + MasterKeyXml.TagSize + plaintext.Length];
            var nonce = payload.AsSpan(0, MasterKeyXml.NonceSize);
            var tag = payload.AsSpan(MasterKeyXml.NonceSize, MasterKeyXml.TagSize);
            var ciphertext = payload.AsSpan(MasterKeyXml.NonceSize + MasterKeyXml.TagSize);
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(masterKey.Bytes, MasterKeyXml.TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, MasterKeyXml.AssociatedData);

            var element = new XElement(
                MasterKeyXml.ElementName,
                new XAttribute(MasterKeyXml.VersionAttribute, MasterKeyXml.Version),
                new XComment(" Chave do Data Protection cifrada com a chave mestra (AES-256-GCM) — SPEC-0008. "),
                new XElement(MasterKeyXml.ValueElementName, Convert.ToBase64String(payload)));
            return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Decifra as chaves gravadas por <see cref="MasterKeyXmlEncryptor"/> (instanciado pelo Data Protection).</summary>
internal sealed class MasterKeyXmlDecryptor : IXmlDecryptor
{
    private readonly DataProtectionMasterKey _masterKey;

    // O Data Protection cria o decifrador pelo nome do tipo, com este construtor.
    public MasterKeyXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _masterKey = services.GetRequiredService<DataProtectionMasterKey>();
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        if (encryptedElement.Name != MasterKeyXml.ElementName
            || (string?)encryptedElement.Attribute(MasterKeyXml.VersionAttribute) != MasterKeyXml.Version
            || encryptedElement.Element(MasterKeyXml.ValueElementName)?.Value is not { } value)
        {
            throw new CryptographicException("Chave do Data Protection em formato desconhecido.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Chave do Data Protection corrompida.", exception);
        }

        if (payload.Length <= MasterKeyXml.NonceSize + MasterKeyXml.TagSize)
        {
            throw new CryptographicException("Chave do Data Protection corrompida.");
        }

        var plaintext = new byte[payload.Length - MasterKeyXml.NonceSize - MasterKeyXml.TagSize];
        try
        {
            using var aes = new AesGcm(_masterKey.Bytes, MasterKeyXml.TagSize);
            // Chave mestra diferente ou conteúdo alterado: AuthenticationTagMismatchException (uma CryptographicException).
            aes.Decrypt(
                payload.AsSpan(0, MasterKeyXml.NonceSize),
                payload.AsSpan(MasterKeyXml.NonceSize + MasterKeyXml.TagSize),
                payload.AsSpan(MasterKeyXml.NonceSize, MasterKeyXml.TagSize),
                plaintext,
                MasterKeyXml.AssociatedData);
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

internal static class MasterKeyXml
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const string Version = "1";
    public const string VersionAttribute = "version";

    public static readonly XName ElementName = "masterKeyEncrypted";
    public static readonly XName ValueElementName = "value";

    /// <summary>Dados associados: amarram o conteúdo cifrado a este uso (não decifra como outra coisa).</summary>
    public static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("Vitrine.DataProtection.KeyRing.v1");
}
