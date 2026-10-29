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
/// IT-02 (SPEC-0014): o check do anel na subida fora de Development considera só elementos <c>&lt;key&gt;</c>. Um
/// <c>&lt;revocation&gt;</c> (gravado sem <c>encryptedSecret</c> pelo próprio <see cref="IKeyManager"/>) não impede a
/// subida; uma <c>&lt;key&gt;</c> em claro continua impedindo. Chaves mestras e o anel são gerados por execução.
/// Ao fim de cada caso, os pools de conexão dos bancos dele são esvaziados: os hosts já foram descartados e as conexões
/// ociosas não ficam ocupando o PostgreSQL compartilhado pela coleção.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class KeyRingRevocationTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly List<string> _databases = [];

    [Theory]
    [Trait("spec", "SPEC-0014:IT-02")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fora_de_Development_anel_com_revocation_nao_impede_a_subida(bool revokeAll)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();

        // Dado um anel cifrado pela chave mestra no PostgreSQL
        await using (var first = Host(connectionString).WithMasterKey(masterKey))
        {
            var keyManager = first.Services.GetRequiredService<IKeyManager>();
            var key = keyManager.CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));

            // E uma revogação gravada pela API real do Data Protection (uma chave ou todas)
            if (revokeAll)
            {
                keyManager.RevokeAllKeys(DateTimeOffset.UtcNow, "SPEC-0014 IT-02");
            }
            else
            {
                keyManager.RevokeKey(key.KeyId, "SPEC-0014 IT-02");
            }
        }

        var ring = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);
        Assert.Contains(ring, xml => XElement.Parse(xml).Name.LocalName == "revocation");
        Assert.Contains(ring, xml => XElement.Parse(xml).Name.LocalName == "key");

        // Quando o host sobe em Production com a mesma chave mestra
        await using var again = Host(connectionString).WithMasterKey(masterKey);
        var exception = Record.Exception(() => again.Services);

        // Então sobe normalmente (a revogação não é tratada como chave em claro)
        Assert.True(exception is null, "O host deveria subir com <revocation> no anel, mas falhou: " + exception?.Message);
        Assert.NotNull(again.Services.GetRequiredService<IFieldProtector>());
    }

    [Fact]
    [Trait("spec", "SPEC-0014:IT-02")]
    public async Task Fora_de_Development_key_em_claro_no_anel_continua_impedindo_a_subida()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();

        // Dado um anel cifrado pela chave mestra e, ao lado, uma <key> em claro (gerada pelo Data Protection sem
        // cifrador e gravada direto no repositório do anel, sem passar pela chave mestra)
        var plainKey = PlaintextKey();
        Assert.Equal("key", plainKey.Name.LocalName);
        await using (var first = Host(connectionString).WithMasterKey(masterKey))
        {
            first.Services.GetRequiredService<IKeyManager>()
                .CreateNewKey(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(90));
            var repository = first.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
            Assert.NotNull(repository);
            repository.StoreElement(plainKey, "chave-em-claro-" + Guid.NewGuid().ToString("N"));
        }

        var ring = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);
        Assert.Contains(ring, xml => xml.Contains(plainKey.Attribute("id")!.Value, StringComparison.Ordinal));

        // Quando o host sobe em Production
        await using var again = Host(connectionString).WithMasterKey(masterKey);
        var exception = Record.Exception(() => again.Services);

        // Então não sobe: a mensagem aponta a chave sem a cifra da chave mestra e não expõe a chave
        Assert.True(exception is not null, "O host subiu com <key> em claro no anel, mas deveria recusar a subida.");
        var failure = string.Join(" | ", Flatten(exception).Select(inner => inner.Message));
        Assert.Contains("sem a cifra da chave mestra", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(masterKey, failure, StringComparison.Ordinal);
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

    /// <summary>Uma <c>&lt;key&gt;</c> real do Data Protection, gerada sem cifrador (segredo em claro no XML).</summary>
    private static XElement PlaintextKey()
    {
        var repository = new CapturingXmlRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName("spec-0014-it-02")
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
