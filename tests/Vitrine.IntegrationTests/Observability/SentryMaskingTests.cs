using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Observability;

/// <summary>
/// UT-02 (SPEC-0007): a configuração real do Sentry da Api (a do <c>Program</c>, sem alterações) mascara ou remove
/// dados pessoais e segredos antes do envio. O DSN é falso e o transporte do SDK é trocado por um que só guarda o
/// envelope serializado — exatamente os bytes que iriam para o Sentry; nada sai para a rede.
/// </summary>
/// <remarks>
/// Fica no projeto de integração porque a configuração é aplicada pelo host da Api (<c>WebApplicationFactory</c>),
/// que exige o PostgreSQL do teste para subir.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed partial class SentryMaskingTests(PostgresFixture postgres)
{
    private const string FakeDsn = "https://chave-publica-falsa@o0.ingest.sentry.io/0";
    private const string Release = "3f2a9c1d8e7b6a5f4e3d2c1b0a9f8e7d6c5b4a39";
    private const string Environment = "staging";

    private const string Email = "marina.souza@example.com";
    private const string Bearer = "Bearer eyJhbGciOiJIUzI1NiJ9.sentry-segredo.assinatura";
    private const string SessionCookie = "vitrine.session=CfDJ8sessao-secreta-sentry";
    private const string QueryToken = "tok_consulta_secreto_123";
    private const string BodyPassword = "senha-do-corpo-2026";

    // Trecho não sensível da mensagem da exceção: identifica o evento do teste entre os enviados.
    private const string Marker = "Falha-UT02 ao autenticar";

    private static readonly string[] Secrets =
        [Email, Bearer, "eyJhbGciOiJIUzI1NiJ9", SessionCookie, "CfDJ8sessao-secreta-sentry", QueryToken, BodyPassword];

    // Valores de prova dos logs via ILogger (B1 da review).
    private const string LogCategory = "Vitrine.Testes.SentryViaILogger";
    private const string LogPassword = "pw-PROBE-123";
    private const string LogAccessToken = "tok-PROBE-456";
    private const string LogBreadcrumbTokenValue = "tok-PROBE-BC-999";

    // Categoria do log de erro com exceção (SPEC-0013:UT-01).
    private const string ExceptionLogCategory = "Vitrine.Testes.SentryExcecaoComSegredo";

    private static readonly string[] LogSecrets = [Email, LogPassword, LogAccessToken, LogBreadcrumbTokenValue];

    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("spec", "SPEC-0007:UT-02")]
    public async Task Evento_e_breadcrumb_saem_mascarados_sem_PII_e_com_release_e_ambiente_da_configuracao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso), release e ambiente vindos da configuração
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithSetting("Sentry:Release", Release)
            .WithSetting("Sentry:Environment", Environment)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var options = factory.Services.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value;
        var hub = factory.Services.GetRequiredService<IHub>();

        // Quando um breadcrumb traz e-mail (na mensagem e nos dados)
        hub.AddBreadcrumb(
            $"Login falhou para {Email}",
            "auth",
            "default",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["email"] = Email, ["destinatario"] = Email, ["rota"] = "/api/v1/auth/login" });

        // E um evento traz e-mail na mensagem e na exceção, cabeçalhos Authorization/Cookie, cookies, query com token e corpo
        var sentryEvent = new SentryEvent(new InvalidOperationException($"{Marker} {Email}"))
        {
            Message = new SentryMessage { Formatted = $"Erro no login de {Email}", Message = $"Erro no login de {Email}" },
        };
        sentryEvent.Request.Method = "POST";
        sentryEvent.Request.Url = "https://vitrine.example/api/v1/auth/login";
        sentryEvent.Request.Headers["Authorization"] = Bearer;
        sentryEvent.Request.Headers["Cookie"] = SessionCookie;
        sentryEvent.Request.Headers["Accept"] = "application/json";
        sentryEvent.Request.Cookies = SessionCookie;
        sentryEvent.Request.QueryString = $"access_token={QueryToken}&email={Email}";
        sentryEvent.Request.Data = new { email = Email, password = BodyPassword };
        sentryEvent.SetExtra("password", BodyPassword);
        sentryEvent.SetExtra("contato", $"falar com {Email}");
        sentryEvent.SetExtra("tentativas", 3);
        hub.CaptureEvent(sentryEvent);
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(Marker, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou o evento do teste ao transporte",
            cancellationToken);

        // Então nenhum envelope enviado (o do teste e os que a Api gerou sozinha a partir de logs de erro) carrega
        // e-mail, credenciais, cookies, token da query nem corpo
        foreach (var sent in transport.EventEnvelopes())
        {
            foreach (var secret in Secrets)
            {
                Assert.False(sent.Contains(secret, StringComparison.OrdinalIgnoreCase), $"\"{secret}\" foi enviado ao Sentry:\n{sent}");
            }
        }

        var envelope = transport.EventEnvelopes().Single(json => json.Contains(Marker, StringComparison.Ordinal));

        // E o que não é sensível continua lá (o evento não foi esvaziado): exceção, cabeçalho comum, extra numérico, rota
        Assert.Contains("InvalidOperationException", envelope, StringComparison.Ordinal);
        Assert.Contains("application/json", envelope, StringComparison.Ordinal);
        Assert.Contains("tentativas", envelope, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/login", envelope, StringComparison.Ordinal);

        // E PII desligada, corpo da requisição nunca anexado, release e ambiente vindos da configuração
        Assert.False(options.SendDefaultPii);
        Assert.Equal(RequestSize.None, options.MaxRequestBodySize);
        Assert.Equal(Release, options.Release);
        Assert.Equal(Environment, options.Environment);
        Assert.Contains($"\"release\":\"{Release}\"", envelope, StringComparison.Ordinal);
        Assert.Contains($"\"environment\":\"{Environment}\"", envelope, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0007:UT-02")]
    public async Task Logs_da_aplicacao_via_ILogger_nao_levam_email_senha_nem_token_para_tags_logentry_ou_breadcrumbs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

        // Quando a aplicação registra um aviso (vira breadcrumb) e depois um erro (vira evento) com propriedades
        // estruturadas sensíveis, pelo ILogger
        LogBreadcrumbToken(logger, LogBreadcrumbTokenValue);
        LogLoginError(logger, Email, LogPassword, LogAccessToken);
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(LogCategory, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou ao transporte o evento do LogError",
            cancellationToken);

        // Então nenhum envelope de evento carrega esses valores — nem em tags, nem em logentry.formatted/message,
        // nem nos breadcrumbs
        foreach (var sent in transport.EventEnvelopes())
        {
            foreach (var secret in LogSecrets)
            {
                Assert.False(sent.Contains(secret, StringComparison.OrdinalIgnoreCase), $"\"{secret}\" foi enviado ao Sentry:\n{sent}");
            }
        }

        // E o evento do erro e o breadcrumb do aviso foram de fato enviados (o teste não passa por omissão)
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(LogCategory, StringComparison.Ordinal));
        Assert.Contains("login de", envelope, StringComparison.Ordinal);
        Assert.Contains("breadcrumb token", envelope, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0013:UT-01")]
    public async Task Excecao_com_o_mesmo_segredo_de_propriedade_sensivel_sai_mascarada_no_Sentry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ExceptionLogCategory);

        // Quando a aplicação registra um erro cuja exceção repete, no texto, o valor de uma propriedade sensível
        // (sem formato de e-mail, Bearer/Basic nem nome=valor)
        LogPasswordWithException(logger, new InvalidOperationException($"falhou para {LogPassword}"), LogPassword);
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(ExceptionLogCategory, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou ao transporte o evento do LogError com exceção",
            cancellationToken);

        // Então nenhum envelope de evento carrega o valor — nem na exceção, nem na mensagem, nem nas tags
        foreach (var sent in transport.EventEnvelopes())
        {
            Assert.False(sent.Contains(LogPassword, StringComparison.OrdinalIgnoreCase), $"\"{LogPassword}\" foi enviado ao Sentry:\n{sent}");
        }

        // E o evento foi de fato enviado, com a exceção (o teste não passa por omissão)
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(ExceptionLogCategory, StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException", envelope, StringComparison.Ordinal);
        Assert.Contains("falhou para", envelope, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0007:UT-02")]
    public async Task Sem_ambiente_configurado_o_Sentry_usa_o_ambiente_do_host()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o DSN definido, mas sem Sentry:Environment
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithSetting("Sentry:Environment", null)
            .WithSetting("SENTRY_ENVIRONMENT", null)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = new RecordingTransport()));

        // Quando a Api sobe
        var options = factory.Services.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value;
        var hostEnvironment = factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();

        // Então o ambiente do Sentry é o do host, em minúsculas, e a PII continua desligada
        Assert.Equal(hostEnvironment.EnvironmentName.ToLowerInvariant(), options.Environment);
        Assert.False(options.SendDefaultPii);
    }

    [Fact]
    [Trait("spec", "SPEC-0007:UT-02")]
    public async Task Sem_DSN_o_Sentry_nao_e_registrado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a Api sem Sentry:Dsn (vazio mesmo que o ambiente do processo de teste o tenha)
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", string.Empty);

        // Quando a Api sobe
        var services = factory.Services;

        // Então nenhum serviço do Sentry é registrado (nem hub, nem cliente)
        Assert.Null(services.GetService<IHub>());
        Assert.Null(services.GetService<ISentryClient>());
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "login de {Email} com {Password} e {AccessToken}")]
    private static partial void LogLoginError(ILogger logger, string email, string password, string accessToken);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "breadcrumb token {Token}")]
    private static partial void LogBreadcrumbToken(ILogger logger, string token);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "{Password}")]
    private static partial void LogPasswordWithException(ILogger logger, Exception exception, string password);

    /// <summary>Transporte do SDK que guarda cada envelope serializado (o que seria enviado), sem rede.</summary>
    private sealed class RecordingTransport : ITransport
    {
        private readonly ConcurrentQueue<(bool IsEvent, string Json)> _envelopes = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            var isEvent = envelope.Items.Any(item => item.TryGetType() == "event");
            _envelopes.Enqueue((isEvent, Encoding.UTF8.GetString(stream.ToArray())));
        }

        public IReadOnlyList<string> EventEnvelopes() => [.. _envelopes.Where(item => item.IsEvent).Select(item => item.Json)];

        public IReadOnlyList<string> All() => [.. _envelopes.Select(item => item.Json)];
    }
}
