using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vitrine.Common.Security;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// IT-08 (SPEC-0008): rotação da chave mestra e subida recusada fora de Development com chave ausente ou errada.
/// O host de teste aplica, na subida, o mesmo passo de schema do modo <c>--migrate</c> (recursos com estado — ADR-0011),
/// que durante a rotação recifra o anel de chaves com a chave atual. As chaves são geradas por execução.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MasterKeyTests(PostgresFixture postgres)
{
    private const string PreviousMasterKey = "DataProtection:PreviousMasterKey";

    [Fact]
    [Trait("spec", "SPEC-0008:IT-08")]
    public async Task Rotacao_com_chave_anterior_recifra_o_anel_e_depois_so_a_nova_chave_mantem_sessao_e_campos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var keyA = SecuritySettings.NewMasterKey();
        var keyB = SecuritySettings.NewMasterKey();
        var plaintext = "segredo-de-teste-" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        System.Net.CookieCollection sessionCookies;

        // Dado chaves do Data Protection gravadas com a chave mestra A, uma sessão e um campo cifrado
        await using (var withA = Host(connectionString).WithMasterKey(keyA))
        {
            using var browser = new Browser(withA, TestAddresses.Client(0));
            using (var register = await browser.RegisterAsync(TestUser.New(), cancellationToken))
            {
                await IdentityApi.AssertStatusAsync(HttpStatusCode.Created, register, cancellationToken);
            }

            sessionCookies = browser.Cookies.GetCookies(Browser.HttpsOrigin);
            await using var context = new ProtectedSecretsContext(connectionString, withA.Services.GetRequiredService<IFieldProtector>());
            await context.CreateTableAsync(cancellationToken);
            context.Secrets.Add(new StoredSecret { Id = id, Secret = plaintext });
            await context.SaveChangesAsync(cancellationToken);
        }

        var ringUnderA = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);

        // Quando o host sobe com A como anterior e B como atual (o passo de migração recifra o anel)
        await using (var rotating = Host(connectionString).WithMasterKey(keyB).WithSetting(PreviousMasterKey, keyA))
        {
            _ = rotating.Services.GetRequiredService<IFieldProtector>();
        }

        var ringUnderB = await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken);
        Assert.Equal(ringUnderA.Count, ringUnderB.Count);
        Assert.All(ringUnderB, xml => Assert.DoesNotContain(xml, ringUnderA));
        Assert.All(ringUnderB, xml =>
        {
            Assert.DoesNotContain(keyA, xml, StringComparison.Ordinal);
            Assert.DoesNotContain(keyB, xml, StringComparison.Ordinal);
        });

        // Então, só com B, a sessão antiga continua válida e o campo antigo é decifrado
        await using var withB = Host(connectionString).WithMasterKey(keyB);
        using var sameBrowser = new Browser(withB, TestAddresses.Client(0));
        sameBrowser.Cookies.Add(sessionCookies);
        using var me = await sameBrowser.GetAsync(IdentityApi.MePath, cancellationToken);
        await IdentityApi.AssertStatusAsync(HttpStatusCode.OK, me, cancellationToken);

        await using var reopened = new ProtectedSecretsContext(connectionString, withB.Services.GetRequiredService<IFieldProtector>());
        var stored = await reopened.Secrets.AsNoTracking().SingleAsync(secret => secret.Id == id, cancellationToken);
        Assert.Equal(plaintext, stored.Secret);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-08")]
    public async Task Fora_de_Development_sem_chave_mestra_o_host_nao_sobe_e_a_mensagem_diz_o_motivo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado a Api em Production sem DataProtection__MasterKey
        await using var factory = Host(connectionString).WithSetting(SecuritySettings.MasterKey, string.Empty);

        // Quando o host sobe, então falha com mensagem que nomeia a configuração ausente
        var failure = StartupFailure(factory);
        Assert.Contains("DataProtection__MasterKey", failure, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:IT-08")]
    public async Task Fora_de_Development_com_chave_valida_mas_errada_para_o_anel_o_host_nao_sobe_sem_expor_a_chave()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var keyA = SecuritySettings.NewMasterKey();
        var wrongKey = SecuritySettings.NewMasterKey();

        // Dado um anel de chaves gravado com a chave mestra A (com um campo cifrado nele)
        await using (var withA = Host(connectionString).WithMasterKey(keyA))
        {
            await using var context = new ProtectedSecretsContext(connectionString, withA.Services.GetRequiredService<IFieldProtector>());
            await context.CreateTableAsync(cancellationToken);
            context.Secrets.Add(new StoredSecret { Id = Guid.NewGuid(), Secret = "segredo-de-teste-" + Guid.NewGuid().ToString("N") });
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.NotEmpty(await DataProtectionKeys.ReadXmlAsync(connectionString, cancellationToken));

        // Quando o host sobe em Production com outra chave, de formato válido, que não decifra nenhuma chave do anel
        await using var factory = Host(connectionString).WithMasterKey(wrongKey);

        // Então não sobe; a mensagem fala da chave mestra e não expõe nenhuma das chaves
        var failure = StartupFailure(factory);
        Assert.Contains("MasterKey", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongKey, failure, StringComparison.Ordinal);
        Assert.DoesNotContain(keyA, failure, StringComparison.Ordinal);
    }

    private static ApiFactory Host(string connectionString) =>
        IdentityHost.Create(connectionString).WithEnvironment(Environments.Production);

    /// <summary>Sobe o host e exige que a subida falhe; devolve as mensagens da exceção e das internas.</summary>
    private static string StartupFailure(ApiFactory factory)
    {
        var exception = Record.Exception(() => factory.Services);
        Assert.True(exception is not null, "O host subiu, mas deveria recusar a subida.");
        return string.Join(" | ", Flatten(exception).Select(inner => inner.Message));
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
}
