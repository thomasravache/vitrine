using System.Net;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vitrine.Common.Security;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Cifra de campos no PostgreSQL real e persistência das chaves do Data Protection (contrato da SPEC-0008). A entidade
/// <see cref="StoredSecret"/> existe só no projeto de testes; o <see cref="IFieldProtector"/> é o registrado no host.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class FieldProtectionTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0008:IT-04")]
    public async Task Campo_IsProtected_grava_cifrado_no_Postgres_e_a_leitura_via_EF_devolve_o_original()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);
        var protector = HostProtector(factory);

        // Dado uma entidade de teste com campo IsProtected
        var plaintext = "segredo-de-teste-" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        await using (var context = new ProtectedSecretsContext(connectionString, protector))
        {
            await context.CreateTableAsync(cancellationToken);

            // Quando gravada no Postgres real
            context.Secrets.Add(new StoredSecret { Id = id, Secret = plaintext });
            await context.SaveChangesAsync(cancellationToken);
        }

        // Então a coluna lida por SQL cru não contém o texto claro (é o valor cifrado com o propósito do campo)
        var raw = await ProtectedSecretsContext.ReadRawAsync(connectionString, id, cancellationToken);
        Assert.False(string.IsNullOrEmpty(raw), "A coluna protegida ficou vazia.");
        Assert.DoesNotContain(plaintext, raw, StringComparison.Ordinal);
        Assert.Equal(plaintext, protector.Unprotect(raw, ProtectedSecretsContext.Purpose));

        // E a leitura via EF devolve o original
        await using (var context = new ProtectedSecretsContext(connectionString, protector))
        {
            var stored = await context.Secrets.AsNoTracking().SingleAsync(secret => secret.Id == id, cancellationToken);
            Assert.Equal(plaintext, stored.Secret);
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-04")]
    public async Task Data_Protection_do_host_cifra_com_AES_256_GCM()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString);

        // Dado o host real; quando a configuração do Data Protection é lida
        var configuration = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.AuthenticatedEncryptorConfiguration;

        // Então o algoritmo das chaves novas é AES-256-GCM (ADR-0006)
        Assert.True(
            configuration is AuthenticatedEncryptorConfiguration { EncryptionAlgorithm: EncryptionAlgorithm.AES_256_GCM },
            $"Data Protection deveria usar AES-256-GCM; configuração atual: {Describe(configuration)}.");
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-05")]
    public async Task Apos_reinicio_com_a_mesma_chave_mestra_a_sessao_continua_valida_e_o_campo_e_decifrado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var masterKey = SecuritySettings.NewMasterKey();
        var plaintext = "segredo-de-teste-" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        CookieCollection sessionCookies;

        // Dado uma sessão ativa e um campo cifrado gravado
        await using (var before = IdentityHost.Create(connectionString).WithMasterKey(masterKey))
        {
            using var browser = new Browser(before, TestAddresses.Client(0));
            using (var register = await browser.RegisterAsync(TestUser.New(), cancellationToken))
            {
                await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, register, cancellationToken);
            }

            sessionCookies = browser.Cookies.GetCookies(Browser.HttpsOrigin);
            Assert.Contains(sessionCookies, cookie => cookie.Name == Vitrine.Identity.Infrastructure.IdentityModule.SessionCookieName);

            await using var context = new ProtectedSecretsContext(connectionString, HostProtector(before));
            await context.CreateTableAsync(cancellationToken);
            context.Secrets.Add(new StoredSecret { Id = id, Secret = plaintext });
            await context.SaveChangesAsync(cancellationToken);
        }

        // E as chaves do Data Protection ficaram no PostgreSQL, protegidas pela chave mestra (nada em claro)
        var keys = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);
        Assert.NotEmpty(keys);
        Assert.All(keys, xml =>
        {
            Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("unencrypted form", xml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(masterKey, xml, StringComparison.Ordinal);
        });

        // Quando o host é reiniciado (novo processo de host) com a mesma chave mestra
        await using var after = IdentityHost.Create(connectionString).WithMasterKey(masterKey);
        using var sameBrowser = new Browser(after, TestAddresses.Client(0));
        sameBrowser.Cookies.Add(sessionCookies);

        // Então a sessão continua válida
        using var me = await sameBrowser.GetAsync(IdentityApi.MePath, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);

        // E o campo é decifrado
        await using var reopened = new ProtectedSecretsContext(connectionString, HostProtector(after));
        var stored = await reopened.Secrets.AsNoTracking().SingleAsync(secret => secret.Id == id, cancellationToken);
        Assert.Equal(plaintext, stored.Secret);
    }

    private static IFieldProtector HostProtector(ApiFactory factory)
    {
        var protector = factory.Services.GetService<IFieldProtector>();
        Assert.True(protector is not null, "O host não registra IFieldProtector.");
        return protector;
    }

    private static string Describe(AlgorithmConfiguration? configuration) => configuration switch
    {
        null => "(padrão do framework: AES-256-CBC + HMACSHA256)",
        AuthenticatedEncryptorConfiguration authenticated => $"{authenticated.EncryptionAlgorithm} / {authenticated.ValidationAlgorithm}",
        _ => configuration.GetType().Name,
    };
}
