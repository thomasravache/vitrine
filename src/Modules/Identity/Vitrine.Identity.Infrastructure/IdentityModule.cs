using JasperFx.CommandLine.Descriptions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Application.CurrentUser;
using Vitrine.Identity.Application.Register;
using Vitrine.Identity.Infrastructure.Persistence;
using Vitrine.Identity.Infrastructure.Sessions;
using Vitrine.Identity.Infrastructure.Users;
using Vitrine.Identity.IntegrationEvents;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vitrine.Identity.Infrastructure;

/// <summary>Registro do módulo Identity no host (ASP.NET Core Identity, DbContext do schema "identity", outbox).</summary>
public static class IdentityModule
{
    /// <summary>Nome do cookie de sessão (HttpOnly, Secure, SameSite=Lax).</summary>
    public const string SessionCookieName = "vitrine.session";

    /// <summary>Registra os serviços do módulo Identity.</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Persistência no schema "identity", integrada ao outbox do Wolverine (ADR-0011, ADR-0014). A connection
        // string é lida no primeiro uso: registrar o módulo não exige banco (ex.: geração do documento OpenAPI).
        services.AddDbContextWithWolverineIntegration<IdentityModuleDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("vitrine")
                ?? throw new InvalidOperationException("ConnectionStrings:vitrine não configurada (o AppHost a fornece)."),
            ConfigureNpgsql));
        services.AddSingleton<ISystemPart, IdentityMigrations>();
        services.ConfigureWolverine(options => options.LocalQueueFor<WorkspaceCreated>().UseDurableInbox());

        // ASP.NET Core Identity com sessão em cookie na mesma origem (ADR-0007).
        services.AddIdentityCore<User>(ConfigureIdentity)
            .AddEntityFrameworkStores<IdentityModuleDbContext>()
            .AddSignInManager();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(ConfigureSessionCookie);

        // Casos de uso e portas da Application.
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<RegisterCommandHandler>();
        services.AddScoped<IdentityUnitOfWork>();
        services.AddScoped<IIdentityUnitOfWork>(provider => provider.GetRequiredService<IdentityUnitOfWork>());
        services.AddScoped<IWorkspaceRepository>(provider => provider.GetRequiredService<IdentityUnitOfWork>());
        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<ISessions, CookieSessions>();
        services.AddScoped<IMeQueries, MeQueries>();

        return services;
    }

    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityModuleDbContext.Schema);

    private static void ConfigureIdentity(IdentityOptions options)
    {
        // Senha: mínimo de 10 caracteres, sem regras de composição (ADR-0007, SPEC-0006).
        options.Password.RequiredLength = RegisterCommandValidator.PasswordMinLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;

        // Bloqueio após 5 falhas seguidas, por 15 minutos.
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        // O e-mail é o nome de usuário: qualquer caractere de e-mail é aceito, e um e-mail é de uma conta só.
        options.User.AllowedUserNameCharacters = string.Empty;
        options.User.RequireUniqueEmail = true;
    }

    private static void ConfigureSessionCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = SessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // API: sem redirecionamento para página de login; a SPA trata 401/403.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
