using JasperFx.CommandLine.Descriptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vitrine.Common.Security;

namespace Vitrine.Api.Security;

/// <summary>
/// Segurança de base do host (SPEC-0008, ADR-0006, ADR-0007): forwarded headers só da rede do proxy, cabeçalhos de
/// segurança, rate limiting de autenticação, Data Protection com chaves no PostgreSQL protegidas pela chave mestra e
/// cifra de campos (<see cref="IFieldProtector"/>).
/// </summary>
internal static class SecurityExtensions
{
    /// <summary>Nome da aplicação no Data Protection: isola as chaves e mantém cookies válidos entre versões e réplicas.</summary>
    public const string DataProtectionApplicationName = "Vitrine";

    /// <summary>Redes (CIDR) do proxy das quais X-Forwarded-For/Proto são aceitos (<c>ForwardedHeaders__KnownNetworks__N</c>).</summary>
    public const string ForwardedKnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    /// <param name="builder">O host da Api.</param>
    /// <param name="generatingOpenApi">
    /// Geração do documento OpenAPI no build (GetDocument.Insider sobe o host, sem banco nem chave mestra — SPEC-0011): o
    /// Data Protection usa um provedor efêmero em memória, sem chave mestra, sem anel no PostgreSQL e sem a checagem do
    /// anel. Em qualquer outra subida, a chave mestra continua obrigatória fora de Development.
    /// </param>
    public static WebApplicationBuilder AddSecurity(this WebApplicationBuilder builder, bool generatingOpenApi)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Sem cabeçalho Server no Kestrel (o Caddy também remove o dele).
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

        // Atrás do Caddy: X-Forwarded-For/Proto valem só vindos das redes configuradas; de qualquer outro IP são ignorados.
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var cidr in configuration.GetSection(ForwardedKnownNetworksKey).GetChildren().Select(child => child.Value))
            {
                if (string.IsNullOrWhiteSpace(cidr))
                {
                    continue;
                }

                options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(cidr.Trim(), out var network)
                    ? network
                    : throw new InvalidOperationException(
                        $"{ForwardedKnownNetworksKey}: \"{cidr}\" não é uma rede CIDR válida (ex.: 10.250.0.0/24)."));
            }
        });

        services.AddSingleton<SecurityHeaders>();
        services.AddAuthRateLimiting();
        if (generatingOpenApi)
        {
            // Só o documento é gerado: nenhum dado é protegido, então nada de chave mestra, banco ou checagem do anel.
            services.AddDataProtection()
                .SetApplicationName(DataProtectionApplicationName)
                .UseEphemeralDataProtectionProvider();
        }
        else
        {
            services.AddDataProtectionKeyRing();
        }

        services.AddSingleton<IFieldProtector, DataProtectionFieldProtector>();

        return builder;
    }

    /// <summary>Middlewares que vêm antes de todo o resto (forwarded headers e cabeçalhos de segurança).</summary>
    public static WebApplication UseEdgeSecurity(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        app.UseSecurityHeaders();
        return app;
    }

    /// <summary>Rate limiting de autenticação (depois do tratamento de exceções, antes da SPA e dos endpoints).</summary>
    public static WebApplication UseAuthRateLimiting(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseLoginEmailPartition();
        app.UseRateLimiter();
        return app;
    }

    private static void AddDataProtectionKeyRing(this IServiceCollection services)
    {
        services.AddOptions<DataProtectionMasterKeyOptions>()
            .BindConfiguration(DataProtectionMasterKeyOptions.Section)
            .Validate<IHostEnvironment>(
                (options, environment) => !string.IsNullOrWhiteSpace(options.MasterKey) || environment.IsDevelopment(),
                DataProtectionMasterKey.MissingMessage)
            .Validate(
                options => (string.IsNullOrWhiteSpace(options.MasterKey) || DataProtectionMasterKey.IsValid(options.MasterKey))
                    && (string.IsNullOrWhiteSpace(options.PreviousMasterKey) || DataProtectionMasterKey.IsValid(options.PreviousMasterKey)),
                DataProtectionMasterKey.InvalidMessage)
            .ValidateOnStart();
        services.AddSingleton<DataProtectionMasterKey>();

        // Tabela própria (schema "security"); a connection string é lida no primeiro uso, como nos módulos.
        services.AddDbContext<DataProtectionKeysDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IConfiguration>().GetConnectionString("vitrine")
                ?? throw new InvalidOperationException("ConnectionStrings:vitrine não configurada (o AppHost a fornece)."),
            DataProtectionKeysDbContext.ConfigureNpgsql));
        services.AddSingleton<ISystemPart, DataProtectionMigrations>();

        services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<DataProtectionKeysDbContext>()
            .UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
            {
                EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
                ValidationAlgorithm = ValidationAlgorithm.HMACSHA256,
            });

        // Cada chave do anel é gravada cifrada pela chave mestra (nunca em claro no banco). Com chave mestra efêmera
        // (só Development sem chave), o anel fica em memória em vez de acumular no banco chaves que ninguém lerá depois.
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
            new ConfigureOptions<KeyManagementOptions>(options =>
            {
                var masterKey = provider.GetRequiredService<DataProtectionMasterKey>();
                options.XmlEncryptor = new MasterKeyXmlEncryptor(masterKey);
                if (masterKey.IsEphemeral)
                {
                    options.XmlRepository = new InMemoryXmlRepository();
                }
            }));

        // Fora de Development, chave mestra que não decifra o anel existente impede a subida (contrato v4).
        services.AddHostedService<DataProtectionKeyRingCheck>();
    }
}
