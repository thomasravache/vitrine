using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vitrine.Api.Security;

/// <summary>
/// Na subida, fora de Development (contrato v4 da SPEC-0008): se o anel de chaves no PostgreSQL tem chaves cifradas
/// pela chave mestra e nenhuma decifra com a atual (nem com a anterior, na rotação), o host não sobe — senão sessões e
/// campos cifrados se perderiam em silêncio e o Data Protection criaria um anel novo. A mensagem nunca traz a chave.
/// </summary>
internal sealed class DataProtectionKeyRingCheck(IServiceScopeFactory scopes, IHostEnvironment environment) : IHostedLifecycleService
{
    public const string WrongKeyMessage =
        "DataProtection:MasterKey (variável DataProtection__MasterKey) não decifra nenhuma chave do anel do Data Protection "
        + "gravado no banco: a chave mestra está errada. Use a chave com que o anel foi gravado (cofre pessoal) ou, durante "
        + "a rotação, informe-a em DataProtection__PreviousMasterKey (docs/runbooks/rotacao-chave-mestra.md).";

    public const string UnprotectedKeyMessage =
        "O anel do Data Protection no banco tem chave sem a cifra da chave mestra (DataProtection__MasterKey): entrada "
        + "gravada sem proteção ou por outro meio. Remova-a (docs/runbooks/rotacao-chave-mestra.md) antes de subir a Api.";

    private const string DataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    // Os mesmos nomes que o XmlKeyManager do Data Protection lê: só <key> sem namespace é chave, e o descritor é o único
    // filho de <key>/<descriptor>; dentro dele o segredo cifrado é <encryptedSecret>, decifrado pelo tipo que o atributo
    // decryptorType nomeia (que precisa ser o MasterKeyXmlDecryptor), e o em claro é <masterKey> (requiresEncryption).
    private static readonly XName KeyElementName = "key";
    private static readonly XName DescriptorElementName = "descriptor";
    private static readonly XName EncryptedSecret = XName.Get("encryptedSecret", DataProtectionNamespace);
    private static readonly XName DecryptorType = "decryptorType";
    private static readonly XName RequiresEncryption = XName.Get("requiresEncryption", DataProtectionNamespace);
    private static readonly XName PlaintextMasterKey = "masterKey";

    // Antes de qualquer StartAsync: o serviço hospedado do Data Protection carrega o anel na subida e, sem chave que o
    // decifre, criaria (e gravaria) uma chave nova — que esconderia o erro.
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var masterKey = scope.ServiceProvider.GetRequiredService<DataProtectionMasterKey>();
        var db = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();

        List<string?> ring;
        try
        {
            ring = await db.DataProtectionKeys.AsNoTracking().Select(key => key.Xml).ToListAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            // Schema ainda não aplicado (o --migrate cria a tabela): não há anel para conferir.
            return;
        }

        // Toda chave do anel precisa estar cifrada pela chave mestra: entrada em claro (ou cifrada por outro meio) no
        // banco fora de Development é recusada. Só elementos <key> (sem namespace, como no Data Protection) carregam
        // segredo; <revocation> (gravado sem encryptedSecret pelo IKeyManager) é ignorado (SPEC-0014). O segredo é
        // conferido no descritor que o Data Protection decifra, não em qualquer ponto da <key> (SPEC-0017).
        var keys = ring.Where(xml => !string.IsNullOrEmpty(xml)).Select(xml => XElement.Parse(xml!))
            .Where(element => element.Name == KeyElementName).ToList();
        var encrypted = new List<XElement>(keys.Count);
        foreach (var key in keys)
        {
            encrypted.AddRange(MasterKeySecrets(key) ?? throw new InvalidOperationException(UnprotectedKeyMessage));
        }

        if (encrypted.Count > 0 && !encrypted.Any(element => CanDecrypt(element, masterKey)))
        {
            throw new InvalidOperationException(WrongKeyMessage);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Os <c>masterKeyEncrypted</c> do descritor da chave, lido como o <c>XmlKeyManager</c> do Data Protection
    /// (<c>key.Element("descriptor").Elements().Single()</c>, decifrado por <c>DecryptElement</c>, que escolhe o decifrador
    /// de cada <c>encryptedSecret</c> pelo <c>decryptorType</c>); <see langword="null"/> se o descritor tiver segredo em
    /// claro (<c>masterKey</c> ou <c>requiresEncryption</c>), algum <c>encryptedSecret</c> fora de
    /// <c>key/descriptor/descriptor</c>, com <c>decryptorType</c> que não seja o <see cref="MasterKeyXmlDecryptor"/> ou sem
    /// <c>masterKeyEncrypted</c>, ou nenhum <c>encryptedSecret</c>.
    /// </summary>
    private static List<XElement>? MasterKeySecrets(XElement key)
    {
        var descriptors = key.Element(DescriptorElementName)?.Elements().ToList();
        if (descriptors is not [var descriptor]
            || descriptor.DescendantsAndSelf().Any(element => element.Name == PlaintextMasterKey || element.Attribute(RequiresEncryption) is not null))
        {
            return null;
        }

        var secrets = new List<XElement>();
        foreach (var encryptedSecret in descriptor.DescendantsAndSelf(EncryptedSecret))
        {
            if (encryptedSecret.Parent != descriptor
                || !IsMasterKeyDecryptor((string?)encryptedSecret.Attribute(DecryptorType))
                || encryptedSecret.Elements().ToList() is not [var secret]
                || secret.Name != MasterKeyXml.ElementName)
            {
                return null;
            }

            secrets.Add(secret);
        }

        return secrets.Count > 0 ? secrets : null;
    }

    /// <summary>
    /// Se o <c>decryptorType</c> resolve para o <see cref="MasterKeyXmlDecryptor"/>, como o Data Protection compara
    /// (<c>TypeExtensions.MatchName</c>: o nome começa pelo nome completo do tipo e resolve para ele), sem depender da
    /// versão do assembly gravada no nome.
    /// </summary>
    private static bool IsMasterKeyDecryptor(string? decryptorTypeName)
    {
        var expected = typeof(MasterKeyXmlDecryptor);
        if (decryptorTypeName is null || !decryptorTypeName.StartsWith(expected.FullName!, StringComparison.Ordinal))
        {
            return false;
        }

        // Como o DefaultTypeNameResolver do Data Protection: Type.GetType lança mesmo com throwOnError: false (tipo genérico
        // inválido, TypeLoadException etc.), e qualquer falha na resolução significa "não é o decifrador da chave mestra".
        try
        {
            return Type.GetType(WithoutAssemblyVersion(decryptorTypeName), throwOnError: false) == expected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Mesmo corte do TypeForwardingActivator.RemoveVersionFromAssemblyName do Data Protection.
    private static string WithoutAssemblyVersion(string typeName)
    {
        const string VersionMarker = ", Version=";
        var start = typeName.IndexOf(VersionMarker, StringComparison.Ordinal);
        while (start != -1)
        {
            var end = typeName.IndexOf(',', start + VersionMarker.Length);
            typeName = end == -1 ? typeName[..start] : typeName.Remove(start, end - start);
            start = typeName.IndexOf(VersionMarker, StringComparison.Ordinal);
        }

        return typeName;
    }

    private static bool CanDecrypt(XElement element, DataProtectionMasterKey masterKey)
    {
        try
        {
            _ = MasterKeyXml.Decrypt(element, masterKey, out _);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Anel de chaves só em memória: usado em Development quando a chave mestra é efêmera, para não acumular no banco
/// chaves que nenhum processo seguinte consegue ler.
/// </summary>
internal sealed class InMemoryXmlRepository : Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository
{
    private readonly List<XElement> _elements = [];
    private readonly Lock _lock = new();

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_lock)
        {
            return [.. _elements.Select(element => new XElement(element))];
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_lock)
        {
            _elements.Add(new XElement(element));
        }
    }
}
