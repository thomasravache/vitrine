using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Vitrine.Api.Security;

/// <summary>Limites de <c>/api/v1/auth/*</c> (<c>RateLimiting__*</c> no ambiente — contrato v2 da SPEC-0008).</summary>
internal sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimiting";

    public LimitOptions AuthPerIp { get; set; } = new();

    public LimitOptions LoginPerEmail { get; set; } = new();

    internal sealed class LimitOptions
    {
        /// <summary>Requisições permitidas por janela de 1 minuto (padrão do contrato: 10).</summary>
        public int PermitLimit { get; set; } = 10;
    }
}

/// <summary>
/// Rate limiting nativo nos endpoints de autenticação (ADR-0007, SPEC-0008 v3): 10 req/min por IP real do cliente nos
/// POST de cadastro e login (o IP vem dos forwarded headers confiáveis — nunca o do Caddy) e 10 logins/min por e-mail
/// (normalizado) em <c>POST /api/v1/auth/login</c>; excedido, 429 com <c>Retry-After</c>. O bloqueio de conta da SPEC-0006
/// (5 falhas → 423) dispara antes do limite por e-mail, que protege e-mails inexistentes e o custo do hash.
/// </summary>
internal static class AuthRateLimiting
{
    public const string LoginPath = "/api/v1/auth/login";
    public const string RegisterPath = "/api/v1/auth/register";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly object LoginEmailKey = new();

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<AuthRateLimitOptions>()
            .BindConfiguration(AuthRateLimitOptions.Section)
            .Validate(options => options.AuthPerIp.PermitLimit > 0 && options.LoginPerEmail.PermitLimit > 0,
                "RateLimiting:AuthPerIp:PermitLimit e RateLimiting:LoginPerEmail:PermitLimit devem ser maiores que zero.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthRateLimitOptions>, IHostEnvironment>((options, limits, environment) =>
        {
            var perIp = limits.Value.AuthPerIp.PermitLimit;
            var perEmail = limits.Value.LoginPerEmail.PermitLimit;

            // Só em Development: a conexão local (Vite do AppHost em 127.0.0.1, onde rodam o E2E e o dev) não conta no
            // limite por IP — toda a suíte E2E sai do mesmo IP. Fora de Development não há exceção; o limite por e-mail vale sempre.
            var exemptLoopback = environment.IsDevelopment();

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    IsLimitedPerIp(context.Request)
                    && !(exemptLoopback && context.Connection.RemoteIpAddress is { } local && IPAddress.IsLoopback(local))
                        ? FixedWindow("ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido"), perIp)
                        : RateLimitPartition.GetNoLimiter(string.Empty)),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    context.Items.TryGetValue(LoginEmailKey, out var email) && email is string normalized
                        ? FixedWindow("email:" + normalized, perEmail)
                        : RateLimitPartition.GetNoLimiter(string.Empty)));
            options.OnRejected = WriteTooManyRequestsAsync;
        });

        return services;
    }

    /// <summary>
    /// Lê o e-mail do corpo de <c>POST /api/v1/auth/login</c> (corpo bufferizado e rebobinado: o model binding lê de novo)
    /// para a partição por e-mail. JSON malformado ou sem e-mail não particiona: o endpoint responde como sempre (400).
    /// </summary>
    public static IApplicationBuilder UseLoginEmailPartition(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (HttpMethods.IsPost(request.Method)
                && request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase)
                && request.HasJsonContentType())
            {
                request.EnableBuffering();
                try
                {
                    using var document = await JsonDocument.ParseAsync(request.Body, default, context.RequestAborted);
                    if (NormalizedEmail(document.RootElement) is { } email)
                    {
                        context.Items[LoginEmailKey] = email;
                    }
                }
                catch (JsonException)
                {
                    // Corpo inválido: o model binding devolve o 400 do contrato.
                }
                finally
                {
                    request.Body.Position = 0;
                }
            }

            await next(context);
        });

    // Contrato v3: só os POST de cadastro e login contam no limite por IP (GET /antiforgery e logout não).
    private static bool IsLimitedPerIp(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && (request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals(RegisterPath, StringComparison.OrdinalIgnoreCase));

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    // Mesma normalização do ASP.NET Core Identity (UpperInvariantLookupNormalizer), com o nome da propriedade sem
    // diferenciar maiúsculas, como o model binding.
    private static string? NormalizedEmail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "email", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { } email
                && !string.IsNullOrWhiteSpace(email))
            {
                return email.Trim().ToUpperInvariant();
            }
        }

        return null;
    }

    private static async ValueTask WriteTooManyRequestsAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata) ? metadata : Window;
        var seconds = Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, (int)Window.TotalSeconds);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Muitas requisições. Tente de novo mais tarde.",
            },
        });
    }
}
