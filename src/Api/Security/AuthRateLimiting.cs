using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Vitrine.MercadoLivre.Presentation;

namespace Vitrine.Api.Security;

/// <summary>
/// Limites de <c>/api/v1/auth/*</c> (<c>RateLimiting__*</c> no ambiente — contrato v2 da SPEC-0008) e do webhook de
/// notificações do ML (SPEC-0023).
/// </summary>
internal sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimiting";

    public LimitOptions AuthPerIp { get; set; } = new();

    public LimitOptions LoginPerEmail { get; set; } = new();

    /// <summary><c>POST /api/v1/mercadolivre/notifications</c> por IP do cliente (padrão do contrato da SPEC-0023: 600/min).</summary>
    public LimitOptions MercadoLivreNotificationsPerIp { get; set; } = new() { PermitLimit = 600 };

    internal sealed class LimitOptions
    {
        /// <summary>Requisições permitidas por janela de 1 minuto (padrão do contrato: 10).</summary>
        public int PermitLimit { get; set; } = 10;
    }
}

/// <summary>
/// Rate limiting nativo nos endpoints de autenticação (ADR-0007, SPEC-0008 v4): 10 req/min por IP real do cliente nos
/// endpoints de cadastro e login (o IP vem dos forwarded headers confiáveis — nunca o do Caddy) e 10 logins/min por
/// e-mail (normalizado); excedido, 429 com <c>Retry-After</c>. A decisão usa o endpoint roteado (nome <c>Login</c> /
/// <c>Register</c>), então <c>/login/</c> e <c>/LOGIN</c> contam igual. O corpo desses endpoints tem teto de 4 KB (413) e
/// o e-mail da partição é o mesmo que o endpoint usa: JSON com <c>email</c> repetido é recusado (400). O bloqueio de
/// conta da SPEC-0006 (5 falhas → 423) dispara antes do limite por e-mail, que protege e-mails inexistentes e o custo do hash.
/// O webhook de notificações do ML (SPEC-0023) tem a sua própria partição por IP (600/min por padrão), separada da de auth.
/// </summary>
internal static class AuthRateLimiting
{
    /// <summary>Nomes dos endpoints do módulo Identity (<c>WithName</c>) sob o limite por IP e o teto de corpo.</summary>
    public const string LoginEndpointName = "Login";
    public const string RegisterEndpointName = "Register";

    /// <summary>Teto do corpo de login e cadastro (contrato v4).</summary>
    public const int MaxBodyBytes = 4 * 1024;

    private const string ValidationFailedCode = "VALIDATION_FAILED";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly object LoginEmailKey = new();
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<AuthRateLimitOptions>()
            .BindConfiguration(AuthRateLimitOptions.Section)
            .Validate(
                options => options.AuthPerIp.PermitLimit > 0
                    && options.LoginPerEmail.PermitLimit > 0
                    && options.MercadoLivreNotificationsPerIp.PermitLimit > 0,
                "RateLimiting:AuthPerIp:PermitLimit, RateLimiting:LoginPerEmail:PermitLimit e "
                    + "RateLimiting:MercadoLivreNotificationsPerIp:PermitLimit devem ser maiores que zero.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthRateLimitOptions>, IHostEnvironment>((options, limits, environment) =>
        {
            var perIp = limits.Value.AuthPerIp.PermitLimit;
            var perEmail = limits.Value.LoginPerEmail.PermitLimit;
            var notificationsPerIp = limits.Value.MercadoLivreNotificationsPerIp.PermitLimit;

            // Só em Development: a conexão local (Vite do AppHost em 127.0.0.1, onde rodam o E2E e o dev) não conta no
            // limite por IP — toda a suíte E2E sai do mesmo IP. Fora de Development não há exceção; o limite por e-mail vale sempre.
            var exemptLoopback = environment.IsDevelopment();

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    IsAuthEndpoint(context)
                    && !(exemptLoopback && context.Connection.RemoteIpAddress is { } local && IPAddress.IsLoopback(local))
                        ? FixedWindow("ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido"), perIp)
                        : RateLimitPartition.GetNoLimiter(string.Empty)),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    context.Items.TryGetValue(LoginEmailKey, out var email) && email is string normalized
                        ? FixedWindow("email:" + normalized, perEmail)
                        : RateLimitPartition.GetNoLimiter(string.Empty)),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    EndpointName(context) == NotificationsEndpoint.EndpointName
                    && !(exemptLoopback && context.Connection.RemoteIpAddress is { } local && IPAddress.IsLoopback(local))
                        ? FixedWindow("ml-notifications-ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido"), notificationsPerIp)
                        : RateLimitPartition.GetNoLimiter(string.Empty)));
            options.OnRejected = WriteTooManyRequestsAsync;
        });

        return services;
    }

    /// <summary>
    /// Nos endpoints de login e cadastro (pelo endpoint roteado): lê o corpo com teto de <see cref="MaxBodyBytes"/>
    /// (413 sem ler além do teto, com ou sem <c>Content-Length</c>), recusa JSON com <c>email</c> repetido (400) e, no
    /// login, recusa JSON sem exatamente um <c>email</c> string não vazio (400, SPEC-0014) e guarda o e-mail normalizado
    /// para a partição por e-mail. O corpo lido volta para o model binding.
    /// JSON que o limitador não consegue ler (ou que não é um objeto) responde 400 aqui: falha fechada.
    /// </summary>
    public static IApplicationBuilder UseLoginEmailPartition(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var endpointName = EndpointName(context);
            if (endpointName is not (LoginEndpointName or RegisterEndpointName))
            {
                await next(context);
                return;
            }

            var request = context.Request;
            if (request.ContentLength > MaxBodyBytes || await ReadCappedAsync(request, context.RequestAborted) is not { } body)
            {
                await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "Corpo da requisição grande demais.");
                return;
            }

            request.Body = new MemoryStream(body, writable: false);
            request.ContentLength = body.Length;

            if (request.HasJsonContentType())
            {
                // Falha fechada (contrato v4.1): JSON que o limitador não lê não segue sem partição — 400 aqui mesmo.
                // O BOM UTF-8 é aceito (o model binding também o aceita).
                List<JsonElement> emails;
                try
                {
                    using var document = JsonDocument.Parse(WithoutUtf8Bom(body));
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        await WriteInvalidBodyAsync(context);
                        return;
                    }

                    emails = [.. EmailProperties(document.RootElement).Select(element => element.Clone())];
                }
                catch (JsonException)
                {
                    await WriteInvalidBodyAsync(context);
                    return;
                }

                if (emails.Count > 1)
                {
                    await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Dados inválidos.", ValidationFailedCode,
                        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["email"] = ["Informe o e-mail uma única vez."] });
                    return;
                }

                if (endpointName == LoginEndpointName)
                {
                    // SPEC-0014: login sem exatamente um e-mail string não vazio não tem partição por e-mail — 400 aqui,
                    // sem chegar ao Identity.
                    if (emails.Count != 1 || Normalize(emails[0]) is not { } email)
                    {
                        await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Dados inválidos.", ValidationFailedCode,
                            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["email"] = ["Informe o e-mail."] });
                        return;
                    }

                    context.Items[LoginEmailKey] = email;
                }
            }

            await next(context);
        });

    private static string? EndpointName(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;

    // Contrato v3/v4: só cadastro e login (endpoint roteado, só POST) contam no limite por IP.
    private static bool IsAuthEndpoint(HttpContext context) =>
        EndpointName(context) is LoginEndpointName or RegisterEndpointName;

    // Lê até MaxBodyBytes + 1 bytes: além do teto devolve null sem consumir o resto do corpo.
    private static async Task<byte[]?> ReadCappedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
            && (read = await request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total > MaxBodyBytes ? null : buffer[..total];
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    // Propriedades "email" do objeto raiz, sem diferenciar maiúsculas (como o model binding da Api).
    private static List<JsonElement> EmailProperties(JsonElement root) =>
        root.ValueKind != JsonValueKind.Object
            ? []
            : [.. root.EnumerateObject()
                .Where(property => string.Equals(property.Name, "email", StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Value)];

    // Mesma normalização do ASP.NET Core Identity (UpperInvariantLookupNormalizer: Normalize() em FormC e maiúsculas
    // invariantes) sobre o e-mail aparado do login: formas Unicode equivalentes caem na mesma partição.
    private static string? Normalize(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } email && !string.IsNullOrWhiteSpace(email)
            ? email.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant()
            : null;

    private static ReadOnlyMemory<byte> WithoutUtf8Bom(byte[] body) =>
        body.AsSpan().StartsWith(Utf8Bom) ? body.AsMemory(Utf8Bom.Length) : body;

    private static Task WriteInvalidBodyAsync(HttpContext context) =>
        WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Dados inválidos.", ValidationFailedCode,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["body"] = ["O corpo deve ser um objeto JSON válido."] });

    private static async Task WriteProblemAsync(
        HttpContext context, int status, string title, string? code = null, IDictionary<string, string[]>? errors = null)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        await context.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    private static async ValueTask WriteTooManyRequestsAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata) ? metadata : Window;
        var seconds = Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, (int)Window.TotalSeconds);

        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        await WriteProblemAsync(context, StatusCodes.Status429TooManyRequests, "Muitas requisições. Tente de novo mais tarde.");
    }
}
