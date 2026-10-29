using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vitrine.Api;
using Vitrine.Common.Security;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.OpenApi;

/// <summary>
/// IT-02 (SPEC-0011): modo de geração do documento OpenAPI (GetDocument.Insider sobe o host no build — `npm run
/// api:generate` e o passo de drift do CI). Contrato assumido de <c>src/Api/OpenApiGeneration.cs</c>, o mesmo ponto que o
/// <c>Program.cs</c> usa:
/// <list type="bullet">
/// <item><c>OpenApiGeneration.IsGeneratingDocument(Assembly? entryAssembly)</c>: verdadeiro só para o assembly de entrada
/// <c>GetDocument.Insider</c>.</item>
/// <item><c>OpenApiGeneration.Configure(WebApplicationBuilder builder, bool generatingOpenApi)</c>: registra a segurança de
/// base (<c>AddSecurity(generatingOpenApi)</c>) e, na geração, desliga a validação do contêiner no build
/// (<c>ValidateOnBuild = false</c>: sem o Wolverine, serviços dos módulos como o outbox não resolvem).</item>
/// </list>
/// O host aqui é montado só com esse ponto, sem PostgreSQL (nenhuma connection string) e sem chave mestra: a configuração
/// do processo (variáveis de ambiente, appsettings) é descartada para nada herdado mascarar o resultado.
/// </summary>
public sealed class OpenApiGenerationModeTests
{
    private const string KeyRingCheckTypeName = "DataProtectionKeyRingCheck";

    [Theory]
    [Trait("spec", "SPEC-0011:IT-02")]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task No_modo_de_geracao_sem_chave_mestra_nem_banco_o_host_sobe_protege_em_memoria_e_nao_checa_o_anel(string environment)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um host da Api sem DataProtection__MasterKey e sem PostgreSQL, com um serviço que não resolve (como o
        // outbox dos módulos sem o Wolverine)
        var builder = HostWithoutKeyNorDatabase(environment);
        builder.Services.AddScoped<NeedsMessagingRuntime>();

        // Quando os serviços são registrados no modo de geração do OpenAPI
        OpenApiGeneration.Configure(builder, generatingOpenApi: true);

        // Então o host constrói e inicia
        await using var app = builder.Build();
        await app.StartAsync(cancellationToken);

        // E o Data Protection protege e desprotege em memória
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("SPEC-0011:IT-02");
        var plaintext = "openapi-" + Guid.NewGuid().ToString("N");
        Assert.Equal(plaintext, protector.Unprotect(protector.Protect(plaintext)));
        var fields = app.Services.GetRequiredService<IFieldProtector>();
        Assert.Equal(plaintext, fields.Unprotect(fields.Protect(plaintext, "SPEC-0011:IT-02"), "SPEC-0011:IT-02"));

        // E nenhuma checagem do anel de chaves é registrada
        var hostedServices = app.Services.GetServices<IHostedService>().Select(service => service.GetType().Name).ToList();
        Assert.DoesNotContain(KeyRingCheckTypeName, hostedServices);

        await app.StopAsync(cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0011:IT-02")]
    public async Task No_modo_normal_em_Production_sem_chave_mestra_a_subida_falha_com_a_mensagem_da_chave()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um host da Api em Production sem DataProtection__MasterKey
        var builder = HostWithoutKeyNorDatabase("Production");

        // Quando os serviços são registrados no modo normal e o host sobe
        OpenApiGeneration.Configure(builder, generatingOpenApi: false);
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var app = builder.Build();
            await app.StartAsync(cancellationToken);
            await app.StopAsync(cancellationToken);
        });

        // Então a subida falha com a mensagem da chave mestra
        Assert.True(failure is not null, "O host subiu sem chave mestra em Production, mas deveria recusar a subida.");
        Assert.Contains("DataProtection__MasterKey", Messages(failure), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0011:IT-02")]
    public void O_modo_de_geracao_e_detectado_so_pelo_assembly_de_entrada_GetDocument_Insider()
    {
        Assert.True(OpenApiGeneration.IsGeneratingDocument(new NamedAssembly("GetDocument.Insider")));
        Assert.False(OpenApiGeneration.IsGeneratingDocument(new NamedAssembly("Vitrine.Api")));
        Assert.False(OpenApiGeneration.IsGeneratingDocument(typeof(OpenApiGenerationModeTests).Assembly));
        Assert.False(OpenApiGeneration.IsGeneratingDocument(null));
    }

    /// <summary>Host da Api (assembly Vitrine.Api) sem nenhuma configuração do processo, em TestServer.</summary>
    private static WebApplicationBuilder HostWithoutKeyNorDatabase(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(OpenApiGeneration).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SecuritySettings.MasterKey] = string.Empty,
        });
        builder.WebHost.UseTestServer();
        return builder;
    }

    private static string Messages(Exception exception)
    {
        var messages = new List<string>();
        var pending = new Stack<Exception>([exception]);
        while (pending.TryPop(out var current))
        {
            messages.Add(current.Message);
            var inners = current is AggregateException aggregate ? aggregate.InnerExceptions : (IEnumerable<Exception>)[];
            if (current.InnerException is { } single && current is not AggregateException)
            {
                inners = [single];
            }

            foreach (var inner in inners)
            {
                pending.Push(inner);
            }
        }

        return string.Join(" | ", messages);
    }

    /// <summary>Dependência que só o runtime de mensageria (Wolverine) forneceria — ausente no modo de geração.</summary>
    internal interface IMessagingRuntime;

    /// <summary>Como o outbox dos módulos: depende de <see cref="IMessagingRuntime"/>, que não está registrado.</summary>
    internal sealed class NeedsMessagingRuntime(IMessagingRuntime runtime)
    {
        public IMessagingRuntime Runtime { get; } = runtime;
    }

    /// <summary>Assembly com nome arbitrário (o de entrada do processo não é controlável no teste).</summary>
    private sealed class NamedAssembly(string name) : Assembly
    {
        public override string FullName => name;

        public override AssemblyName GetName() => new(name);
    }
}
