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
        return new EncryptedXmlInfo(MasterKeyXml.Encrypt(plaintextElement, masterKey.Current), typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>
/// Decifra as chaves gravadas por <see cref="MasterKeyXmlEncryptor"/> (instanciado pelo Data Protection) com a chave
/// mestra atual ou, durante a rotação, com a anterior.
/// </summary>
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
        return MasterKeyXml.Decrypt(encryptedElement, _masterKey, out _);
    }
}

/// <summary>Formato do XML cifrado pela chave mestra: <c>&lt;masterKeyEncrypted version="1"&gt;&lt;value&gt;nonce|tag|cifra&lt;/value&gt;</c>.</summary>
internal static class MasterKeyXml
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const string Version = "1";

    public static readonly XName ElementName = "masterKeyEncrypted";

    private static readonly XName VersionAttribute = "version";
    private static readonly XName ValueElementName = "value";

    /// <summary>Dados associados: amarram o conteúdo cifrado a este uso (não decifra como outra coisa).</summary>
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("Vitrine.DataProtection.KeyRing.v1");

    public static XElement Encrypt(XElement plaintextElement, byte[] key)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            var payload = new byte[NonceSize + TagSize + plaintext.Length];
            var nonce = payload.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), AssociatedData);

            return new XElement(
                ElementName,
                new XAttribute(VersionAttribute, Version),
                new XComment(" Chave do Data Protection cifrada com a chave mestra (AES-256-GCM) - SPEC-0008. "),
                new XElement(ValueElementName, Convert.ToBase64String(payload)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decifra com a chave atual e, se ela não servir, com a anterior (<paramref name="usedPrevious"/>). Formato
    /// desconhecido, conteúdo alterado ou nenhuma chave que sirva: <see cref="CryptographicException"/>.
    /// </summary>
    public static XElement Decrypt(XElement encryptedElement, DataProtectionMasterKey masterKey, out bool usedPrevious)
    {
        if (encryptedElement.Name != ElementName
            || (string?)encryptedElement.Attribute(VersionAttribute) != Version
            || encryptedElement.Element(ValueElementName)?.Value is not { } value)
        {
            throw new CryptographicException("Chave do Data Protection em formato desconhecido.");
        }

        var payload = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, payload, out var length) || length <= NonceSize + TagSize)
        {
            throw new CryptographicException("Chave do Data Protection corrompida.");
        }

        payload = payload[..length];
        try
        {
            usedPrevious = false;
            return Decrypt(payload, masterKey.Current);
        }
        catch (AuthenticationTagMismatchException) when (masterKey.Previous is not null)
        {
            usedPrevious = true;
            return Decrypt(payload, masterKey.Previous);
        }
    }

    private static XElement Decrypt(byte[] payload, byte[] key)
    {
        var plaintext = new byte[payload.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            // Chave mestra diferente ou conteúdo alterado: AuthenticationTagMismatchException (uma CryptographicException).
            aes.Decrypt(
                payload.AsSpan(0, NonceSize),
                payload.AsSpan(NonceSize + TagSize),
                payload.AsSpan(NonceSize, TagSize),
                plaintext,
                AssociatedData);
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
