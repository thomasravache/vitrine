using JasperFx.CommandLine.Descriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Application.Accounts;
using Vitrine.MercadoLivre.Application.Connections;
using Vitrine.MercadoLivre.Infrastructure.Diagnostics;
using Vitrine.MercadoLivre.Infrastructure.OAuth;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.MercadoLivre.Infrastructure;

/// <summary>
/// Registro do módulo MercadoLivre no host (SPEC-0020): configuração <c>MercadoLivre:*</c>, DbContext do schema
/// <c>mercadolivre</c> com outbox, cliente HTTP do ML e casos de uso de conexão de contas.
/// </summary>
public static class MercadoLivreModule
{
    /// <summary>Nome do <c>HttpClient</c> que fala com a API do ML (<c>ApiBaseUrl</c>), com Http.Resilience.</summary>
    public const string HttpClientName = "mercadolivre";

    /// <summary>Meter das métricas do módulo (<c>vitrine.mercadolivre.*</c>).</summary>
    public const string MeterName = "Vitrine.MercadoLivre";

    /// <summary>Propósito do <c>IFieldProtector</c> para tokens e verifier (SPEC-0008).</summary>
    public const string TokensProtectionPurpose = "mercadolivre.tokens";

    /// <summary>Tempo máximo de uma chamada ao ML, com as tentativas (contrato da SPEC-0020: 10 s).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cron (UTC) da limpeza diária das autorizações pendentes: todo dia às 03:00.</summary>
    public const string CleanupSchedule = "0 3 * * *";

    /// <summary>Registra os serviços do módulo MercadoLivre.</summary>
    public static IServiceCollection AddMercadoLivreModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // MercadoLivre:* (MercadoLivre__* no ambiente). Sem ClientId/ClientSecret/RedirectUri a Api sobe e os endpoints
        // respondem 503 ML_NOT_CONFIGURED; URLs não oficiais em Production impedem a subida.
        services.AddOptions<MercadoLivreOptions>().BindConfiguration(MercadoLivreOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MercadoLivreOptions>, MercadoLivreOptionsValidator>();

        // Persistência no schema "mercadolivre", integrada ao outbox do Wolverine (ADR-0011, ADR-0014), com tokens e
        // verifier cifrados pelo IFieldProtector do host. A connection string é lida no primeiro uso.
        services.AddDbContextWithWolverineIntegration<MercadoLivreModuleDbContext>(options => options
            .UseNpgsql(
                configuration.GetConnectionString("vitrine")
                    ?? throw new InvalidOperationException("ConnectionStrings:vitrine não configurada (o AppHost a fornece)."),
                ConfigureNpgsql));
        // Depois da configuração do Wolverine (que também troca a fábrica de chaves do modelo): modelo por protetor.
        services.ConfigureDbContext<MercadoLivreModuleDbContext>(options =>
            options.ReplaceService<IModelCacheKeyFactory, ProtectorModelCacheKeyFactory>());
        services.AddSingleton<ISystemPart, MercadoLivreMigrations>();
        services.ConfigureWolverine(options =>
        {
            options.LocalQueueFor<MercadoLivreAccountConnected>().UseDurableInbox();
            options.LocalQueueFor<MercadoLivreAccountDisconnected>().UseDurableInbox();
            options.Discovery.IncludeType(typeof(CleanupPendingAuthorizationsHandler));
        });

        // Cliente HTTP do ML: URLs oficiais por padrão; até 10 s por chamada, com as tentativas. O POST /oauth/token
        // nunca é repetido (o code é de uso único). O pipeline padrão do ServiceDefaults (30 s, repete POST) é trocado
        // por um próprio: RemoveAllResilienceHandlers é a forma documentada de sobrepor o ConfigureHttpClientDefaults
        // num cliente só, ainda marcada como experimental (EXTEXP0001) no Microsoft.Extensions.Http.Resilience.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName)
            .RemoveAllResilienceHandlers()
#pragma warning restore EXTEXP0001
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.TotalRequestTimeout.Timeout = RequestTimeout;
                resilience.AttemptTimeout.Timeout = RequestTimeout;
                resilience.Retry.DisableForUnsafeHttpMethods();
            });

        // Casos de uso e portas da Application.
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddSingleton<IConnectionMetrics, MercadoLivreMetrics>();
        services.AddScoped<IMercadoLivreOAuth, MercadoLivreOAuthClient>();
        services.AddScoped<MercadoLivreUnitOfWork>();
        services.AddScoped<IMercadoLivreUnitOfWork>(provider => provider.GetRequiredService<MercadoLivreUnitOfWork>());
        services.AddScoped<IMercadoLivreAccounts>(provider => provider.GetRequiredService<MercadoLivreUnitOfWork>());
        services.AddScoped<IPendingAuthorizations>(provider => provider.GetRequiredService<MercadoLivreUnitOfWork>());
        services.AddScoped<StartConnectionHandler>();
        services.AddScoped<CompleteConnectionHandler>();
        services.AddScoped<ListAccountsHandler>();
        services.AddScoped<DisconnectAccountHandler>();

        return services;
    }

    /// <summary>
    /// Mensagens recorrentes do módulo (ADR-0014), registradas pelo host no <c>UseWolverine</c> (o agendamento registra
    /// serviços, o que uma extensão <c>ConfigureWolverine</c> não pode fazer): limpeza diária das autorizações
    /// pendentes, durável e disparada uma vez por cluster.
    /// </summary>
    public static WolverineOptions AddMercadoLivreSchedules(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Schedules.ScheduleRecurring<CleanupPendingAuthorizations>(CleanupSchedule);
        return options;
    }

    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", MercadoLivreModuleDbContext.Schema);
}
