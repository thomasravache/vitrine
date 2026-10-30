using JasperFx.CommandLine.Descriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Polly;
using Vitrine.Common.Security;
using Vitrine.MercadoLivre.Application.Abstractions;
using Vitrine.MercadoLivre.Application.Accounts;
using Vitrine.MercadoLivre.Application.Connections;
using Vitrine.MercadoLivre.Infrastructure.Api;
using Vitrine.MercadoLivre.Infrastructure.Diagnostics;
using Vitrine.MercadoLivre.Infrastructure.OAuth;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;
using Vitrine.MercadoLivre.PublicApi;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.MercadoLivre.Infrastructure;

/// <summary>
/// Registro do módulo MercadoLivre no host (SPEC-0020, SPEC-0021): configuração <c>MercadoLivre:*</c>, DbContext do
/// schema <c>mercadolivre</c> com outbox, clientes HTTP do ML, casos de uso de conexão de contas e o gateway autenticado
/// (<see cref="IMercadoLivreItems"/>).
/// </summary>
public static class MercadoLivreModule
{
    /// <summary>Nome do <c>HttpClient</c> que fala com a API do ML (<c>ApiBaseUrl</c>), com Http.Resilience.</summary>
    public const string HttpClientName = "mercadolivre";

    /// <summary>
    /// <c>HttpClient</c> das chamadas autenticadas à API do ML (SPEC-0021): <c>AddStandardResilienceHandler</c> (opções
    /// nomeadas <c>mercadolivre-api-standard</c>) e o handler que anexa o token.
    /// </summary>
    public const string ApiHttpClientName = "mercadolivre-api";

    /// <summary><c>HttpClient</c> da renovação do token (<c>POST /oauth/token</c>, SPEC-0021): sem retry.</summary>
    public const string OAuthHttpClientName = "mercadolivre-oauth";

    /// <summary>Meter das métricas do módulo (<c>vitrine.mercadolivre.*</c>).</summary>
    public const string MeterName = "Vitrine.MercadoLivre";

    /// <summary><c>ActivitySource</c> dos spans do gateway da API do ML (SPEC-0021); o host o assina no OpenTelemetry.</summary>
    public const string ActivitySourceName = "Vitrine.MercadoLivre";

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
        services.AddDbContextWithWolverineIntegration<MercadoLivreModuleDbContext>((provider, options) => options
            .UseNpgsql(
                configuration.GetConnectionString("vitrine")
                    ?? throw new InvalidOperationException("ConnectionStrings:vitrine não configurada (o AppHost a fornece)."),
                ConfigureNpgsql)
            .UseFieldProtection(provider.GetRequiredService<IFieldProtector>()));
        services.AddSingleton<ISystemPart, MercadoLivreMigrations>();
        services.ConfigureWolverine(options =>
        {
            options.LocalQueueFor<MercadoLivreAccountConnected>().UseDurableInbox();
            options.LocalQueueFor<MercadoLivreAccountDisconnected>().UseDurableInbox();
            options.LocalQueueFor<MercadoLivreAccountReauthorizationRequired>().UseDurableInbox();
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

        // Gateway autenticado da API do ML (SPEC-0021, ADR-0010): mercadolivre-api com 3 tentativas no total (Retry-After
        // limitado a 30 s), 10 s por tentativa, 60 s no total e circuit breaker padrão; o token é anexado depois do
        // pipeline, então cada tentativa usa o token vigente. O refresh (mercadolivre-oauth) nunca é retentado.
#pragma warning disable EXTEXP0001
        var api = services.AddHttpClient(ApiHttpClientName).RemoveAllResilienceHandlers();
        api.AddStandardResilienceHandler(ConfigureApiResilience);
        api.AddHttpMessageHandler<MercadoLivreAuthenticationHandler>();
        services.AddHttpClient(OAuthHttpClientName)
            .RemoveAllResilienceHandlers()
#pragma warning restore EXTEXP0001
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.TotalRequestTimeout.Timeout = RequestTimeout;
                resilience.AttemptTimeout.Timeout = RequestTimeout;
                resilience.Retry.ShouldHandle = _ => PredicateResult.False();
            });
        services.AddTransient<MercadoLivreAuthenticationHandler>();
        services.AddSingleton<TokenRefreshPolicy>();
        services.AddSingleton<MercadoLivreTokenRefresher>();
        services.AddSingleton<MercadoLivreAccessTokenProvider>();
        services.AddSingleton<IMercadoLivreAccessTokenProvider>(provider => provider.GetRequiredService<MercadoLivreAccessTokenProvider>());
        services.AddScoped<IMercadoLivreItems, MercadoLivreItemsGateway>();

        // Casos de uso e portas da Application.
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddSingleton<MercadoLivreMetrics>();
        services.AddSingleton<IConnectionMetrics>(provider => provider.GetRequiredService<MercadoLivreMetrics>());
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

    /// <summary>Maior espera aceita de um <c>Retry-After</c> do ML (SPEC-0021).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    // 3 tentativas no total (emenda v1.1), 10 s por tentativa e 60 s no total. MaxDelay não limita o Retry-After: o
    // gerador de espera padrão (que lê o cabeçalho) é envolvido por um teto de 30 s.
    private static void ConfigureApiResilience(HttpStandardResilienceOptions resilience)
    {
        resilience.Retry.MaxRetryAttempts = 2;
        resilience.Retry.ShouldRetryAfterHeader = true;
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);

        var retryAfter = resilience.Retry.DelayGenerator;
        resilience.Retry.DelayGenerator = async arguments =>
        {
            var delay = retryAfter is null ? null : await retryAfter(arguments);
            return delay > MaxRetryAfter ? MaxRetryAfter : delay;
        };
    }

    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", MercadoLivreModuleDbContext.Schema);
}
