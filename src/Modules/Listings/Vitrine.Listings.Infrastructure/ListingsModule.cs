using JasperFx.CommandLine.Descriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vitrine.Listings.Application.Browsing;
using Vitrine.Listings.Application.Sync;
using Vitrine.Listings.Infrastructure.Diagnostics;
using Vitrine.Listings.Infrastructure.Messaging;
using Vitrine.Listings.Infrastructure.Persistence;
using Vitrine.MercadoLivre.IntegrationEvents;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.Listings.Infrastructure;

/// <summary>
/// Registro do módulo Listings no host (SPEC-0022): configuração <c>Listings:*</c>, DbContext do schema <c>listings</c>
/// integrado ao Wolverine, handlers duráveis dos eventos do MercadoLivre e das sincronizações, agendador da
/// reconciliação e casos de uso dos endpoints.
/// </summary>
public static class ListingsModule
{
    /// <summary>Meter das métricas do módulo (<c>vitrine.listings.*</c>); o host o assina no OpenTelemetry.</summary>
    public const string MeterName = "Vitrine.Listings";

    /// <summary>Registra os serviços do módulo Listings.</summary>
    public static IServiceCollection AddListingsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ListingsOptions>()
            .BindConfiguration(ListingsOptions.SectionName)
            .Validate(options => options.ReconciliationInterval > TimeSpan.Zero, "Listings:ReconciliationInterval deve ser positivo.")
            .Validate(options => options.ManualSyncCooldown >= TimeSpan.Zero, "Listings:ManualSyncCooldown não pode ser negativo.")
            .ValidateOnStart();

        // Persistência no schema "listings", integrada ao Wolverine (ADR-0011, ADR-0014). A connection string é lida no
        // primeiro uso: registrar o módulo não exige banco (ex.: geração do documento OpenAPI).
        services.AddDbContextWithWolverineIntegration<ListingsModuleDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("vitrine")
                ?? throw new InvalidOperationException("ConnectionStrings:vitrine não configurada (o AppHost a fornece)."),
            ConfigureNpgsql));
        services.AddSingleton<ISystemPart, ListingsMigrations>();

        // Filas locais duráveis (ADR-0014). Os eventos de conta já têm fila declarada pelo módulo MercadoLivre (produtor);
        // MercadoLivreItemChanged ganha a sua aqui, no primeiro consumidor (SPEC-0022, emenda v1.1).
        services.ConfigureWolverine(options =>
        {
            options.LocalQueueFor<MercadoLivreItemChanged>().UseDurableInbox();
            options.LocalQueueFor<SyncListings>().UseDurableInbox();
            options.LocalQueueFor<ReconcileListings>().UseDurableInbox();
            options.Discovery.IncludeType(typeof(AccountEventsHandler));
            options.Discovery.IncludeType(typeof(ItemChangedHandler));
            options.Discovery.IncludeType(typeof(SyncListingsHandler));
            options.Discovery.IncludeType(typeof(ReconcileListingsHandler));
        });

        // Reconciliação a cada Listings:ReconciliationInterval (SPEC-0022 §4).
        services.AddHostedService<ReconciliationScheduler>();

        // Casos de uso e portas da Application.
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddSingleton<ListingsMetrics>();
        services.AddSingleton(provider => new ListingsSyncSettings(provider.GetRequiredService<IOptions<ListingsOptions>>().Value.ManualSyncCooldown));
        services.AddScoped<IListingsReadModel, ListingsReadModel>();
        services.AddScoped<ISyncStates, SyncStates>();
        services.AddScoped<ISyncScheduler, WolverineSyncScheduler>();
        services.AddScoped<ListListingsHandler>();
        services.AddScoped<ListingsSummaryHandler>();
        services.AddScoped<RequestSyncHandler>();

        return services;
    }

    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", ListingsModuleDbContext.Schema);
}
