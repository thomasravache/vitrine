using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Vitrine.Common.Security;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// IT-01 (SPEC-0017): o check do anel na subida fora de Development exige o <c>encryptedSecret</c> (com
/// <c>masterKeyEncrypted</c>) na posição em que o Data Protection o lê (<c>key/descriptor/descriptor/encryptedSecret</c>),
/// e não em qualquer profundidade da <c>&lt;key&gt;</c>; e filtra as chaves pelo <see cref="XName"/> <c>key</c> sem
/// namespace, como o Data Protection. Chaves mestras e o anel são gerados por execução. Ao fim de cada caso, os pools de
/// conexão dos bancos dele são esvaziados para não ocupar o PostgreSQL compartilhado pela coleção.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class KeyRingDescriptorTests(PostgresFixture postgres) : IAsyncDisposable
{
    private const string NullXmlDecryptorType =
        "Microsoft.AspNetCore.DataProtection.XmlEncryption.NullXmlDecryptor, Microsoft.AspNetCore.DataProtection";

    private static readonly XNamespace DataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    private readonly List<string> _databases = [];

    [Fact]
    [Trait("spec", "SPEC-0017:IT-01")]
    public async Task Fora_de_Development_key_em_claro_com_encryptedSecret_fora_do_descritor_impede_a_subida()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();

        // Dado um anel cifrado pela chave mestra no PostgreSQL
        await using (var first = Host(connectionString).WithMasterKey(masterKey))
        {
            first.Services.GetRequiredService<IKeyManager>()
                .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        }

        var ring = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);
        var realSecret = ring.Select(XElement.Parse).Where(element => element.Name == "key")
            .SelectMany(element => element.Descendants(DataProtectionNamespace + "encryptedSecret"))
            .First(element => element.Element("masterKeyEncrypted") is not null);

        // E uma <key> real com o descritor em claro que carrega, fora do descritor (num filho <junk>), uma cópia do
        // encryptedSecret válido (cifrado pela chave mestra) de outra chave
        var forged = PlaintextKey();
        Assert.Equal("key", forged.Name);
        Assert.Empty(forged.Descendants(DataProtectionNamespace + "encryptedSecret"));
        forged.Add(new XElement("junk", new XElement(realSecret)));
        var forgedId = Guid.Parse(forged.Attribute("id")!.Value);
        await StoreAsync(connectionString, masterKey, forged);

        // Quando o host sobe em Production com a mesma chave mestra
        await using var again = Host(connectionString).WithMasterKey(masterKey);
        var exception = Record.Exception(() => again.Services);

        // Então não sobe: a mensagem aponta a chave sem a cifra da chave mestra e não expõe a chave
        if (exception is null)
        {
            // Só aqui o host subiu: consultar o IKeyManager não relança a falha de subida esperada.
            Assert.Fail(
                "O host subiu com <key> de descritor em claro (encryptedSecret só fora do descritor), mas deveria recusar "
                + "a subida. Data Protection carregou a chave em claro: " + LoadsKey(again, forgedId) + ".");
        }

        var failure = string.Join(" | ", Flatten(exception).Select(inner => inner.Message));
        Assert.Contains("sem a cifra da chave mestra", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(masterKey, failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Emenda v1.2: um <c>encryptedSecret</c> cujo <c>decryptorType</c> é o <c>NullXmlDecryptor</c> (que devolve o
    /// conteúdo sem decifrar) não é cifra da chave mestra, mesmo com um <c>masterKeyEncrypted</c> dentro. O anel também
    /// tem chave legítima (que a chave mestra decifra), então a recusa não vem da checagem de chave errada. Re-review:
    /// um caso isola a checagem do <c>decryptorType</c> (sem nenhum <c>masterKey</c> no descritor) e um
    /// <c>decryptorType</c> que não resolve exige a mesma mensagem da checagem, não a do framework.
    /// </summary>
    [Theory]
    [Trait("spec", "SPEC-0017:IT-01")]
    [InlineData(NullDecryptorForgery.Isolated)]
    [InlineData(NullDecryptorForgery.CopiedFromLegitimate)]
    [InlineData(NullDecryptorForgery.BesidePlaintextMasterKey)]
    [InlineData(NullDecryptorForgery.NullWithoutMasterKeyElement)]
    [InlineData(NullDecryptorForgery.UnresolvableDecryptorType)]
    public async Task Fora_de_Development_key_com_encryptedSecret_do_NullXmlDecryptor_impede_a_subida(NullDecryptorForgery forgery)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();

        // Dado um anel com uma chave legítima, cifrada pela chave mestra
        var legitimate = await LegitimateEncryptedSecretAsync(connectionString, masterKey, cancellationToken);

        // E uma <key> real cujo descritor tem um encryptedSecret do NullXmlDecryptor com o segredo em claro
        var forged = PlaintextKey();
        var descriptor = Assert.Single(forged.Element("descriptor")!.Elements());
        var plainMasterKey = descriptor.Element("masterKey")!;
        var strippedMasterKey = new XElement(plainMasterKey);
        strippedMasterKey.Attributes().Where(attribute => attribute.Name.LocalName == "requiresEncryption"
            || attribute.IsNamespaceDeclaration).Remove();
        Assert.NotNull(strippedMasterKey.Element("value"));

        switch (forgery)
        {
            case NullDecryptorForgery.Isolated:
                // (a) o encryptedSecret Null no lugar do <masterKey>, com o segredo em claro dentro de masterKeyEncrypted
                plainMasterKey.ReplaceWith(NullEncryptedSecret(new XElement("masterKeyEncrypted", strippedMasterKey)));
                break;
            case NullDecryptorForgery.CopiedFromLegitimate:
                // (b) cópia do encryptedSecret legítimo com o decryptorType trocado e o conteúdo trocado pelo segredo em claro
                var copied = new XElement(legitimate);
                copied.SetAttributeValue("decryptorType", NullXmlDecryptorType);
                var encryptedMasterKey = copied.Element("masterKeyEncrypted")!;
                encryptedMasterKey.RemoveNodes();
                encryptedMasterKey.Add(strippedMasterKey);
                plainMasterKey.ReplaceWith(copied);
                break;
            case NullDecryptorForgery.BesidePlaintextMasterKey:
                // (c) <masterKey> em claro sem o atributo requiresEncryption, ao lado de um encryptedSecret Null
                plainMasterKey.ReplaceWith(
                    strippedMasterKey,
                    NullEncryptedSecret(new XElement("masterKeyEncrypted", new XElement("value", Convert.ToBase64String(Guid.NewGuid().ToByteArray())))));
                break;
            case NullDecryptorForgery.NullWithoutMasterKeyElement:
                // (d) guarda da checagem do decryptorType isolada: encryptedSecret Null sem nenhum elemento <masterKey>
                // no descritor, só um <notMasterKey> dentro de masterKeyEncrypted
                plainMasterKey.ReplaceWith(NullEncryptedSecret(new XElement(
                    "masterKeyEncrypted",
                    new XElement("notMasterKey", new XElement("value", Convert.ToBase64String(Guid.NewGuid().ToByteArray()))))));
                break;
            case NullDecryptorForgery.UnresolvableDecryptorType:
                // (e) cópia do encryptedSecret legítimo (conteúdo intacto) com um decryptorType genérico malformado, que faz
                // Type.GetType lançar: a recusa precisa vir da checagem, com a mesma mensagem
                var malformed = new XElement(legitimate);
                malformed.SetAttributeValue("decryptorType", "Vitrine.Api.Security.MasterKeyXmlDecryptor[[System.String]], Vitrine.Api");
                plainMasterKey.ReplaceWith(malformed);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null);
        }

        Assert.DoesNotContain(forged.Descendants(), element => element.Attributes().Any(attribute => attribute.Name.LocalName == "requiresEncryption"));
        Assert.Single(descriptor.Elements(DataProtectionNamespace + "encryptedSecret"));
        var forgedId = Guid.Parse(forged.Attribute("id")!.Value);
        await StoreAsync(connectionString, masterKey, forged);

        // Quando o host sobe em Production com a mesma chave mestra
        await using var again = Host(connectionString).WithMasterKey(masterKey);
        var exception = Record.Exception(() => again.Services);

        // Então não sobe: a mensagem aponta a chave sem a cifra da chave mestra e não expõe a chave
        if (exception is null)
        {
            // Só aqui o host subiu: consultar o IKeyManager não relança a falha de subida esperada.
            Assert.Fail(
                "O host subiu com <key> de encryptedSecret de outro decifrador (" + forgery + "), mas deveria recusar a "
                + "subida. Data Protection carregou a chave: " + LoadsKey(again, forgedId) + ".");
        }

        var failure = string.Join(" | ", Flatten(exception).Select(inner => inner.Message));
        Assert.Contains("sem a cifra da chave mestra", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(masterKey, failure, StringComparison.Ordinal);
    }

    public enum NullDecryptorForgery
    {
        Isolated,
        CopiedFromLegitimate,
        BesidePlaintextMasterKey,
        NullWithoutMasterKeyElement,
        UnresolvableDecryptorType,
    }

    /// <summary>
    /// A §3 da spec troca o filtro de <c>Name.LocalName == "key"</c> pelo <see cref="XName"/> <c>key</c> sem namespace,
    /// igual ao Data Protection. Consequência exigida: um elemento <c>key</c> em outro namespace não é chave para o Data
    /// Protection (que o ignora) nem para o check, logo não impede a subida e a chave nele não é carregada.
    /// </summary>
    [Fact]
    [Trait("spec", "SPEC-0017:IT-01")]
    public async Task Fora_de_Development_key_em_claro_em_outro_namespace_e_ignorada_como_pelo_Data_Protection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();

        // Dado um anel cifrado pela chave mestra e, ao lado, uma <key> em claro movida para outro namespace
        var foreign = PlaintextKey();
        foreign.Name = XName.Get("key", "urn:vitrine:spec-0017:outro-namespace");
        var foreignId = Guid.Parse(foreign.Attribute("id")!.Value);
        await using (var first = Host(connectionString).WithMasterKey(masterKey))
        {
            first.Services.GetRequiredService<IKeyManager>()
                .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        }

        await StoreAsync(connectionString, masterKey, foreign);

        // Quando o host sobe em Production
        await using var again = Host(connectionString).WithMasterKey(masterKey);
        var exception = Record.Exception(() => again.Services);

        // Então sobe, e o Data Protection não carrega a chave do outro namespace
        Assert.True(exception is null, "O host deveria subir com <key> de outro namespace (ignorada pelo Data Protection), mas falhou: " + exception?.Message);
        Assert.False(LoadsKey(again, foreignId), "O Data Protection carregou a <key> de outro namespace.");
    }

    [Theory]
    [Trait("spec", "SPEC-0017:IT-01")]
    [InlineData(Revocation.None)]
    [InlineData(Revocation.One)]
    [InlineData(Revocation.All)]
    public async Task Fora_de_Development_anel_valido_continua_subindo(Revocation revocation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();

        // Dado um anel cifrado pela chave mestra (com ou sem revogações gravadas pela API real do Data Protection)
        await using (var first = Host(connectionString).WithMasterKey(masterKey))
        {
            var keyManager = first.Services.GetRequiredService<IKeyManager>();
            var key = keyManager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
            if (revocation == Revocation.One)
            {
                keyManager.RevokeKey(key.KeyId, "SPEC-0017 IT-01");
            }
            else if (revocation == Revocation.All)
            {
                keyManager.RevokeAllKeys(DateTimeOffset.UtcNow, "SPEC-0017 IT-01");
            }
        }

        var ring = (await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken)).Select(XElement.Parse).ToList();
        Assert.Contains(ring, element => element.Name == "key");
        Assert.Equal(revocation != Revocation.None, ring.Any(element => element.Name == "revocation"));

        // Quando o host sobe em Production com a mesma chave mestra
        await using var again = Host(connectionString).WithMasterKey(masterKey);
        var exception = Record.Exception(() => again.Services);

        // Então sobe normalmente
        Assert.True(exception is null, "O host deveria subir com anel válido, mas falhou: " + exception?.Message);
        Assert.NotNull(again.Services.GetRequiredService<IFieldProtector>());
    }

    public enum Revocation
    {
        None,
        One,
        All,
    }

    public ValueTask DisposeAsync()
    {
        foreach (var connectionString in _databases)
        {
            using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        _databases.Add(connectionString);
        return connectionString;
    }

    private static ApiFactory Host(string connectionString) =>
        IdentityHost.Create(connectionString).WithEnvironment(Environments.Production);

    /// <summary>Grava o elemento direto no repositório do anel no PostgreSQL, sem passar pela chave mestra.</summary>
    private static async Task StoreAsync(string connectionString, string masterKey, XElement element)
    {
        await using var host = Host(connectionString).WithMasterKey(masterKey);
        var repository = host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
        Assert.NotNull(repository);
        repository.StoreElement(element, "spec-0017-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>Cria no PostgreSQL uma chave legítima cifrada pela chave mestra e devolve o encryptedSecret dela.</summary>
    private static async Task<XElement> LegitimateEncryptedSecretAsync(string connectionString, string masterKey, CancellationToken cancellationToken)
    {
        await using (var first = Host(connectionString).WithMasterKey(masterKey))
        {
            first.Services.GetRequiredService<IKeyManager>()
                .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        }

        var ring = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);
        return ring.Select(XElement.Parse).Where(element => element.Name == "key")
            .SelectMany(element => element.Descendants(DataProtectionNamespace + "encryptedSecret"))
            .First(element => element.Element("masterKeyEncrypted") is not null);
    }

    private static XElement NullEncryptedSecret(XElement content) =>
        new(DataProtectionNamespace + "encryptedSecret", new XAttribute("decryptorType", NullXmlDecryptorType), content);

    private static bool LoadsKey(ApiFactory host, Guid keyId) =>
        host.Services.GetRequiredService<IKeyManager>().GetAllKeys().Any(key => key.KeyId == keyId);

    /// <summary>Uma <c>&lt;key&gt;</c> real do Data Protection, gerada sem cifrador (segredo em claro no descritor).</summary>
    private static XElement PlaintextKey()
    {
        var repository = new CapturingXmlRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName("spec-0017-it-01")
            .AddKeyManagementOptions(options => options.XmlRepository = repository);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
        return Assert.Single(repository.Elements);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        var inners = exception is AggregateException aggregate ? aggregate.InnerExceptions : (IEnumerable<Exception>)[];
        if (exception.InnerException is { } single && exception is not AggregateException)
        {
            inners = [single];
        }

        foreach (var inner in inners.SelectMany(Flatten))
        {
            yield return inner;
        }
    }

    private sealed class CapturingXmlRepository : IXmlRepository
    {
        public List<XElement> Elements { get; } = [];

        public IReadOnlyCollection<XElement> GetAllElements() => [.. Elements];

        public void StoreElement(XElement element, string friendlyName) => Elements.Add(new XElement(element));
    }
}
