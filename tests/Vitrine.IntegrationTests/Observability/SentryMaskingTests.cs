using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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

    // Categoria e valores do log de erro com Exception.Data (SPEC-0015:IT-01).
    private const string ExceptionDataLogCategory = "Vitrine.Testes.SentryExceptionData";
    private const string DataToken = "abc-PROBE";

    // Categoria e valores falsos dos casos de Exception.Data da SPEC-0018:IT-01.
    private const string ExceptionDataCaseCategory = "Vitrine.Testes.SentryExceptionDataCasos";
    private const string MessageDataToken = "P4-MSG-PROBE";
    private const string ContextPassword = "ctx-pw-PROBE";
    private const string ExtraPassword = "extra-pw-PROBE";
    private const string AnonymousPassword = "anon-pw-PROBE";
    private const string RecordPassword = "record-pw-PROBE";
    private const string PairPassword = "kvp-pw-PROBE";

    // Limite do LogError com grafo de referências compartilhadas (alvo < 1 s; margem para CI lento).
    private static readonly TimeSpan GraphTimeLimit = TimeSpan.FromSeconds(5);

    // Categoria dos casos de formato difícil da SPEC-0018:IT-02.
    private const string HardDataCaseCategory = "Vitrine.Testes.SentryExceptionDataDificil";

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
    [Trait("spec", "SPEC-0015:IT-01")]
    public async Task Exception_Data_sai_mascarado_em_mechanism_data_no_Sentry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ExceptionDataLogCategory);

        // Quando a aplicação registra um erro com propriedade sensível cuja exceção carrega, em Exception.Data,
        // o mesmo valor (chave de nome comum) e um token (chave de nome sensível)
        var exception = new InvalidOperationException("falha com dados anexados");
        exception.Data["Detalhe"] = $"valor {LogPassword}";
        exception.Data["Token"] = DataToken;
        LogErrorWithExceptionData(logger, exception, LogPassword);
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(ExceptionDataLogCategory, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou ao transporte o evento do LogError com Exception.Data",
            cancellationToken);

        // Então nenhum envelope de evento carrega o valor sensível nem o token
        foreach (var sent in transport.EventEnvelopes())
        {
            Assert.False(sent.Contains(LogPassword, StringComparison.OrdinalIgnoreCase), $"\"{LogPassword}\" foi enviado ao Sentry:\n{sent}");
            Assert.False(sent.Contains(DataToken, StringComparison.OrdinalIgnoreCase), $"\"{DataToken}\" foi enviado ao Sentry:\n{sent}");
        }

        // E o evento foi de fato enviado e mechanism.data mantém a chave Detalhe (o Data não foi descartado inteiro)
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(ExceptionDataLogCategory, StringComparison.Ordinal));
        var mechanismData = MechanismData(envelope);
        Assert.True(mechanismData.ContainsKey("Detalhe"), $"mechanism.data sem a chave Detalhe:\n{envelope}");
    }

    [Theory]
    [Trait("spec", "SPEC-0018:IT-01")]
    [InlineData("valor-sensivel-na-mensagem")]
    [InlineData("contexts-e-extras")]
    [InlineData("objetos-em-chave-neutra")]
    public async Task Exception_Data_com_segredo_fora_de_texto_simples_nao_chega_ao_Sentry(string caso)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var category = $"{ExceptionDataCaseCategory}.{caso}";
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        // Quando a aplicação registra um erro (sem propriedade sensível no log) com a exceção do caso
        var (exception, secrets, assertNonSensitive) = ExceptionDataCase(caso);
        LogErrorWithStep(logger, exception, caso);
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(category, StringComparison.Ordinal)),
            FlushTimeout,
            $"o Sentry não entregou ao transporte o evento do caso {caso}",
            cancellationToken);

        // Então nenhum envelope de evento carrega os segredos do caso
        foreach (var sent in transport.EventEnvelopes())
        {
            foreach (var secret in secrets)
            {
                Assert.False(sent.Contains(secret, StringComparison.OrdinalIgnoreCase), $"\"{secret}\" foi enviado ao Sentry:\n{sent}");
            }
        }

        // E o evento foi de fato enviado, com as chaves não sensíveis do caso (nada foi descartado inteiro)
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(category, StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException", envelope, StringComparison.Ordinal);
        assertNonSensitive(envelope);
    }

    /// <summary>Monta a exceção, os segredos e a verificação das chaves não sensíveis de cada caso da SPEC-0018:IT-01.</summary>
    private static (Exception Exception, string[] Secrets, Action<string> AssertNonSensitive) ExceptionDataCase(string caso)
    {
        switch (caso)
        {
            case "valor-sensivel-na-mensagem":
                {
                    // (a) Valor de chave sensível de Exception.Data repetido no texto da exceção.
                    var exception = new InvalidOperationException($"falha com {MessageDataToken}");
                    exception.Data["Token"] = MessageDataToken;
                    exception.Data["Etapa"] = "sincronizacao";
                    return (exception, [MessageDataToken], envelope =>
                    {
                        Assert.Contains("falha com", envelope, StringComparison.Ordinal);
                        var mechanismData = MechanismData(envelope);
                        Assert.True(mechanismData.ContainsKey("Token"), $"mechanism.data sem a chave Token:\n{envelope}");
                        Assert.Equal("sincronizacao", mechanismData.GetValueOrDefault("Etapa"));
                    }
                    );
                }

            case "contexts-e-extras":
                {
                    // (b) Chaves com prefixo sentry: viram contexts/extra no SDK, com valores não-string.
                    var exception = new InvalidOperationException("falha com contexto anexado");
                    exception.Data["sentry:context:auth"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["password"] = ContextPassword,
                        ["provedor"] = "provedor-local",
                    };
                    exception.Data["sentry:extra"] = new { Password = ExtraPassword, Tentativas = "tentativas-3" };
                    return (exception, [ContextPassword, ExtraPassword], envelope =>
                    {
                        Assert.Contains("provedor-local", envelope, StringComparison.Ordinal);
                        Assert.Contains("tentativas-3", envelope, StringComparison.Ordinal);
                    }
                    );
                }

            case "objetos-em-chave-neutra":
                {
                    // (c) Objetos (tipo anônimo, record, struct) com segredo em chaves de nome comum.
                    var exception = new InvalidOperationException("falha com objetos anexados");
                    exception.Data["Detalhe"] = new { Password = AnonymousPassword, Rota = "rota-anonima" };
                    exception.Data["Registro"] = new ProbeCredential("usuario-registro", RecordPassword);
                    exception.Data["Par"] = new KeyValuePair<string, string>("password", PairPassword);
                    return (exception, [AnonymousPassword, RecordPassword, PairPassword], envelope =>
                    {
                        var mechanismData = MechanismData(envelope);
                        foreach (var key in new[] { "Detalhe", "Registro", "Par" })
                        {
                            Assert.True(mechanismData.ContainsKey(key), $"mechanism.data sem a chave {key}:\n{envelope}");
                        }
                    }
                    );
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caso), caso, "caso desconhecido");
        }
    }

    [Theory]
    [Trait("spec", "SPEC-0018:IT-02")]
    [InlineData("delimitadores-no-valor")]
    [InlineData("objeto-aninhado-sob-nome-sensivel")]
    [InlineData("valor-de-objeto-na-mensagem")]
    [InlineData("json-element")]
    public async Task Objetos_de_Exception_Data_de_formato_dificil_nao_vazam_segredo_para_o_Sentry(string caso)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var category = $"{HardDataCaseCategory}.{caso}";
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        // Quando a aplicação registra um erro (sem propriedade sensível no log) com a exceção do caso
        var (exception, secrets) = HardDataCase(caso);
        LogErrorWithStep(logger, exception, caso);
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(category, StringComparison.Ordinal)),
            FlushTimeout,
            $"o Sentry não entregou ao transporte o evento do caso {caso}",
            cancellationToken);

        // Então nenhum envelope de evento carrega os segredos do caso
        foreach (var sent in transport.EventEnvelopes())
        {
            foreach (var secret in secrets)
            {
                Assert.False(sent.Contains(secret, StringComparison.OrdinalIgnoreCase), $"\"{secret}\" foi enviado ao Sentry:\n{sent}");
            }
        }

        // E o evento foi de fato enviado, com a exceção
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(category, StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException", envelope, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0018:IT-02")]
    public async Task Colecao_cuja_enumeracao_lanca_em_Exception_Data_nao_quebra_o_LogError_e_o_evento_e_enviado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var category = $"{HardDataCaseCategory}.colecao-que-lanca";
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        // Quando a exceção registrada traz em Exception.Data uma coleção cuja enumeração lança (ex.: já descartada)
        var exception = new InvalidOperationException("falha com colecao descartada");
        exception.Data["Itens"] = new DisposedEnumerable();
        var thrown = Record.Exception(() => LogErrorWithStep(logger, exception, "colecao-que-lanca"));

        // Então o LogError não lança
        Assert.Null(thrown);

        // E o evento é enviado
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(category, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou ao transporte o evento da coleção que lança",
            cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0018:IT-02")]
    public async Task Valores_numericos_ou_curtos_sob_nome_sensivel_nao_corrompem_a_mensagem_nem_o_HResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var category = $"{HardDataCaseCategory}.valores-curtos";
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        // Quando a exceção registrada traz, sob nomes sensíveis, valores numéricos curtos que aparecem em outros textos
        const string OriginalMessage = "falha ao processar 13 itens do lote 2031";
        var exception = new InvalidOperationException(OriginalMessage);
        exception.Data["TokenTtl"] = 1;
        exception.Data["PasswordMinLength"] = 3;
        var expectedHResult = $"0x{exception.HResult:X8}";
        LogErrorWithStep(logger, exception, "valores-curtos");
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(category, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou ao transporte o evento dos valores curtos",
            cancellationToken);

        // Então exception.value sai igual ao original (sem *** no meio das palavras) e mechanism.data.HResult intacto
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(category, StringComparison.Ordinal));
        Assert.Equal([OriginalMessage], ExceptionValues(envelope));
        Assert.Equal(expectedHResult, MechanismData(envelope).GetValueOrDefault("HResult"));
    }

    [Theory]
    [Trait("spec", "SPEC-0018:IT-02")]
    [InlineData("getter-que-lanca")]
    [InlineData("tostring-que-lanca")]
    [InlineData("tostring-nulo-e-tipo-da-plataforma")]
    [InlineData("chave-de-dicionario-que-lanca")]
    [InlineData("iformattable-sob-nome-sensivel")]
    [InlineData("colecoes-preguicosas")]
    [InlineData("ciclos")]
    [InlineData("aninhamento-profundo")]
    [InlineData("excecoes-internas")]
    [InlineData("muitas-entradas-e-membros")]
    [InlineData("json-array-e-string")]
    [InlineData("pares")]
    [InlineData("extras-e-contexts-do-usuario")]
    [InlineData("struct-de-dominio")]
    [InlineData("uri-com-credenciais")]
    public async Task Valores_que_nao_dao_para_ler_com_seguranca_nao_quebram_o_LogError_nem_vazam_segredo(string caso)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var category = $"{HardDataCaseCategory}.defensivo.{caso}";
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        // Quando a aplicação registra um erro cuja exceção (ou o escopo) traz o valor difícil do caso
        var (exception, secrets) = DefensiveDataCase(caso, hub);
        var thrown = Record.Exception(() => LogErrorWithStep(logger, exception, caso));

        // Então o LogError não lança
        Assert.Null(thrown);

        // E o evento é enviado
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(category, StringComparison.Ordinal)),
            FlushTimeout,
            $"o Sentry não entregou ao transporte o evento do caso {caso}",
            cancellationToken);
        var envelope = transport.EventEnvelopes().Single(json => json.Contains(category, StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException", envelope, StringComparison.Ordinal);

        // E nenhum envelope de evento carrega os segredos do caso
        foreach (var sent in transport.EventEnvelopes())
        {
            foreach (var secret in secrets)
            {
                Assert.False(sent.Contains(secret, StringComparison.OrdinalIgnoreCase), $"\"{secret}\" foi enviado ao Sentry:\n{sent}");
            }
        }
    }

    [Fact]
    [Trait("spec", "SPEC-0018:IT-02")]
    public async Task Grafo_com_referencias_compartilhadas_em_Exception_Data_nao_trava_o_LogError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a configuração do Sentry da Api com DSN definido (falso) e o transporte de teste
        var transport = new RecordingTransport();
        await using var factory = IdentityHost.Create(await postgres.CreateDatabaseAsync(cancellationToken))
            .WithSetting("Sentry:Dsn", FakeDsn)
            .WithServices(services => services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport));
        var hub = factory.Services.GetRequiredService<IHub>();
        var category = $"{HardDataCaseCategory}.grafo-compartilhado";
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        // Quando a exceção traz um grafo de 10 níveis em que cada nível tem 10 chaves apontando para o MESMO filho
        object level = new Dictionary<string, object>(StringComparer.Ordinal) { ["folha"] = "valor-comum" };
        for (var depth = 0; depth < 10; depth++)
        {
            var child = level;
            level = Enumerable.Range(0, 10).ToDictionary(index => $"chave{index}", _ => child, StringComparer.Ordinal);
        }

        var exception = new InvalidOperationException("falha com grafo compartilhado");
        exception.Data["Grafo"] = level;

        // (o LogError roda numa thread à parte para o teste falhar por tempo em vez de travar a suíte)
        Exception? thrown = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var worker = new Thread(() =>
        {
            try
            {
                LogErrorWithStep(logger, exception, "grafo-compartilhado");
            }
#pragma warning disable CA1031 // O teste verifica que nada é lançado; qualquer exceção é registrada e reprovada abaixo.
            catch (Exception caught)
#pragma warning restore CA1031
            {
                thrown = caught;
            }
        })
        { IsBackground = true };
        worker.Start();
        var finished = worker.Join(GraphTimeLimit);
        stopwatch.Stop();

        // Então o LogError termina dentro do limite e não lança
        Assert.True(finished, $"LogError não terminou em {GraphTimeLimit.TotalSeconds} s com o grafo compartilhado");
        Assert.True(stopwatch.Elapsed < GraphTimeLimit, $"LogError levou {stopwatch.Elapsed.TotalMilliseconds} ms");
        Assert.Null(thrown);

        // E o evento é enviado
        await hub.FlushAsync(FlushTimeout);
        await Eventually.TrueAsync(
            () => transport.EventEnvelopes().Any(json => json.Contains(category, StringComparison.Ordinal)),
            FlushTimeout,
            "o Sentry não entregou ao transporte o evento do grafo compartilhado",
            cancellationToken);
    }

    /// <summary>Segredo falso de prova (montado em tempo de execução).</summary>
    private static string Probe(string id) => $"{id}-PROBE";

    /// <summary>Monta a exceção e os segredos de cada caso defensivo da SPEC-0018:IT-02.</summary>
    private static (Exception Exception, string[] Secrets) DefensiveDataCase(string caso, IHub hub)
    {
        switch (caso)
        {
            case "getter-que-lanca":
                {
                    // Objeto com getter que lança e senha repetida no texto da exceção.
                    var secret = Probe("getter");
                    var exception = new InvalidOperationException($"falha com {secret}");
                    exception.Data["Conta"] = new ThrowingGetterAccount(secret);
                    return (exception, [secret]);
                }

            case "tostring-que-lanca":
                {
                    // Objeto sem membros públicos cujo ToString lança, sob nome comum e sob nome sensível.
                    var secret = Probe("opaco");
                    var exception = new InvalidOperationException("falha com objeto opaco");
                    exception.Data["Opaco"] = new ThrowingToString(secret);
                    exception.Data["TokenOpaco"] = new ThrowingToString(secret);
                    return (exception, [secret]);
                }

            case "tostring-nulo-e-tipo-da-plataforma":
                {
                    // Objeto sem membros públicos cujo ToString devolve null e tipo da plataforma (Uri) com senha na query.
                    var secret = Probe("nulo");
                    var uriSecret = Probe("uri");
                    var exception = new InvalidOperationException("falha com tipos opacos");
                    exception.Data["Nulo"] = new NullToString(secret);
                    exception.Data["Endereco"] = new Uri($"https://vitrine.example/entrar?password={uriSecret}");
                    return (exception, [secret, uriSecret]);
                }

            case "chave-de-dicionario-que-lanca":
                {
                    // Dicionário com chave cujo ToString lança, ao lado de uma chave sensível repetida na mensagem.
                    var secret = Probe("hashkey");
                    var exception = new InvalidOperationException($"falha com {secret}");
                    exception.Data["Mapa"] = new Hashtable { [new ThrowingToString("chave")] = "valor-comum", ["password"] = secret };
                    return (exception, [secret]);
                }

            case "iformattable-sob-nome-sensivel":
                {
                    // Valor IFormattable (não string) sob nome sensível, repetido na mensagem da exceção.
                    var secret = Probe("formattable");
                    var exception = new InvalidOperationException($"falha com {secret}");
                    exception.Data["Token"] = new ProbeFormattable(secret);
                    return (exception, [secret]);
                }

            case "colecoes-preguicosas":
                {
                    // Enumeração preguiçosa e IQueryable: não são enumeradas (viram ***).
                    var lazySecret = Probe("lazy");
                    var querySecret = Probe("query");
                    var exception = new InvalidOperationException("falha com colecoes preguicosas");
                    exception.Data["Itens"] = Enumerable.Range(0, 3).Select(_ => lazySecret);
                    exception.Data["Consulta"] = new[] { querySecret }.AsQueryable();
                    return (exception, [lazySecret, querySecret]);
                }

            case "ciclos":
                {
                    // Ciclos em dicionário, lista e objeto, com segredo sob nome sensível em cada um.
                    var dictionarySecret = Probe("ciclo-dic");
                    var listSecret = Probe("ciclo-lista");
                    var nodeSecret = Probe("ciclo-obj");
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal) { ["nome"] = "dicionario", ["password"] = dictionarySecret };
                    dictionary["proprio"] = dictionary;
                    var list = new List<object> { new Dictionary<string, object>(StringComparer.Ordinal) { ["token"] = listSecret } };
                    list.Add(list);
                    var node = new CyclicNode { Nome = "no", Senha = nodeSecret };
                    node.Proximo = node;
                    var exception = new InvalidOperationException("falha com ciclos");
                    exception.Data["Dicionario"] = dictionary;
                    exception.Data["Lista"] = list;
                    exception.Data["No"] = node;
                    return (exception, [dictionarySecret, listSecret, nodeSecret]);
                }

            case "aninhamento-profundo":
                {
                    // Dicionários aninhados além do limite de profundidade, com segredo no fundo sob nome comum.
                    var secret = Probe("fundo");
                    object nested = new Dictionary<string, object>(StringComparer.Ordinal) { ["valor"] = secret };
                    for (var level = 0; level < 12; level++)
                    {
                        nested = new Dictionary<string, object>(StringComparer.Ordinal) { ["nivel"] = nested };
                    }

                    var exception = new InvalidOperationException("falha com aninhamento profundo");
                    exception.Data["Profundo"] = nested;
                    return (exception, [secret]);
                }

            case "excecoes-internas":
                {
                    // AggregateException e cadeia longa de InnerException, com token de Data repetido nas mensagens.
                    var aggregateSecret = Probe("agregada");
                    var innerSecret = Probe("interna");
                    Exception chain = new InvalidOperationException("fim da cadeia");
                    for (var level = 0; level < 12; level++)
                    {
                        chain = new InvalidOperationException($"nivel {level}", chain);
                    }

                    var inner = new InvalidOperationException($"interna com {innerSecret}", chain);
                    inner.Data["Token"] = innerSecret;
                    var aggregated = new InvalidOperationException($"agregada com {aggregateSecret}");
                    aggregated.Data["Token"] = aggregateSecret;
                    var exception = new InvalidOperationException("falha com excecoes internas", new AggregateException(aggregated, inner));
                    return (exception, [aggregateSecret, innerSecret]);
                }

            case "muitas-entradas-e-membros":
                {
                    // Coleção e dicionário com mais de 100 itens e objeto com mais de 100 membros, com segredo sob nome sensível.
                    var dictionarySecret = Probe("muitas-chaves");
                    var listSecret = Probe("muitos-itens");
                    var wideSecret = Probe("muitos-membros");
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal) { ["password"] = dictionarySecret };
                    for (var index = 0; index < 150; index++)
                    {
                        dictionary[$"chave{index:000}"] = "valor-comum";
                    }

                    var list = new List<object> { new { Senha = listSecret } };
                    list.AddRange(Enumerable.Range(0, 150).Select(index => (object)$"item{index:000}"));
                    var exception = new InvalidOperationException("falha com muitas entradas");
                    exception.Data["Mapa"] = dictionary;
                    exception.Data["Lista"] = list;
                    exception.Data["Largo"] = new WideObject { Senha = wideSecret };
                    return (exception, [dictionarySecret, listSecret, wideSecret]);
                }

            case "json-array-e-string":
                {
                    // JsonElement array com objeto que tem password e JsonElement string sob nome sensível repetido na mensagem.
                    var arraySecret = Probe("json-array");
                    var stringSecret = Probe("json-string");
                    using var array = JsonDocument.Parse($"[{{\"password\":\"{arraySecret}\"}}, \"texto\", 3]");
                    using var text = JsonDocument.Parse($"\"{stringSecret}\"");
                    var exception = new InvalidOperationException($"falha com {stringSecret}");
                    exception.Data["Lista"] = array.RootElement.Clone();
                    exception.Data["Token"] = text.RootElement.Clone();
                    return (exception, [arraySecret, stringSecret]);
                }

            case "pares":
                {
                    // KeyValuePair com valor escalar, com objeto e com chave sensível; coleção de pares que não é IDictionary.
                    var pairSecret = Probe("par");
                    var collectionSecret = Probe("pares");
                    var objectSecret = Probe("par-objeto");
                    var exception = new InvalidOperationException($"falha com {collectionSecret}");
                    exception.Data["Tentativas"] = new KeyValuePair<string, int>("tentativas", 3);
                    exception.Data["ParSensivel"] = new KeyValuePair<string, string>("password", pairSecret);
                    exception.Data["ParObjeto"] = new KeyValuePair<string, object>("conta", new { Senha = objectSecret });
                    exception.Data["Pares"] = new List<KeyValuePair<string, string>> { new("password", collectionSecret), new("nome", "valor-comum") };
                    return (exception, [pairSecret, collectionSecret, objectSecret]);
                }

            case "extras-e-contexts-do-usuario":
                {
                    // Extra e context do escopo, gravados pela aplicação, com objeto do domínio.
                    var extraSecret = Probe("extra-escopo");
                    var contextSecret = Probe("context-escopo");
                    hub.ConfigureScope(scope =>
                    {
                        scope.SetExtra("perfil", new ProbeProfile("loja-extra", extraSecret));
                        scope.Contexts["perfil"] = new ProbeProfile("loja-context", contextSecret);
                    });
                    return (new InvalidOperationException("falha com escopo"), [extraSecret, contextSecret]);
                }

            case "uri-com-credenciais":
                {
                    // (g) Uri com credenciais (UserInfo) em Data e dentro de objeto do domínio.
                    var credencial = string.Concat("uri-", "PROBE21");
                    var endereco = new Uri($"postgres://admin:{credencial}@db:5432/x");
                    var exception = new InvalidOperationException("falha com conexao");
                    exception.Data["Banco"] = endereco;
                    exception.Data["Conexao"] = new ProbeConnection("principal", endereco);
                    return (exception, [credencial]);
                }

            case "struct-de-dominio":
                {
                    // Struct do domínio com senha, repetida na mensagem.
                    var secret = Probe("struct");
                    var exception = new InvalidOperationException($"falha com {secret}");
                    exception.Data["Credencial"] = new ProbeStructCredential("usuario-struct", secret);
                    return (exception, [secret]);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caso), caso, "caso desconhecido");
        }
    }

    /// <summary>Monta a exceção e os segredos de cada caso de formato difícil da SPEC-0018:IT-02.</summary>
    private static (Exception Exception, string[] Secrets) HardDataCase(string caso)
    {
        switch (caso)
        {
            case "delimitadores-no-valor":
                {
                    // (a) Senhas com os delimitadores do ToString de record/anônimo: }, ], ", Nome =" e quebra de linha.
                    var exception = new InvalidOperationException("falha com delimitadores");
                    exception.Data["Registro"] = new ProbeCredential("usuario-chave", "Abc}def-PROBE01");
                    exception.Data["Detalhe"] = new { Password = "Xy[zz-PROBE01b]" };
                    exception.Data["Outro"] = new ProbeCredential("usuario-virgula", "a, Foo = comma-PROBE17");
                    exception.Data["Linha"] = new { Password = "linha1\nnl-PROBE17" };
                    return (exception, ["def-PROBE01", "Xy[zz", "zz-PROBE01b", "comma-PROBE17", "nl-PROBE17"]);
                }

            case "objeto-aninhado-sob-nome-sensivel":
                {
                    // (b) Objeto aninhado sob nome sensível: o segredo está numa propriedade de nome comum.
                    var exception = new InvalidOperationException("falha com objeto aninhado");
                    exception.Data["Detalhe"] = new { Credentials = new { Valor = "nested-PROBE02" } };
                    return (exception, ["nested-PROBE02"]);
                }

            case "valor-de-objeto-na-mensagem":
                {
                    // (c) Password de objeto repetido no texto da exceção.
                    const string Valor = "objmsg-PROBE03";
                    var exception = new InvalidOperationException($"falha com {Valor} no texto");
                    exception.Data["Detalhe"] = new { Password = Valor };
                    return (exception, [Valor]);
                }

            case "json-element":
                {
                    // (d) JsonElement objeto com "password".
                    var exception = new InvalidOperationException("falha com json anexado");
                    using var document = JsonDocument.Parse("{\"password\":\"je-PROBE13\"}");
                    exception.Data["Corpo"] = document.RootElement.Clone();
                    return (exception, ["je-PROBE13"]);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caso), caso, "caso desconhecido");
        }
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "erro {Password}")]
    private static partial void LogErrorWithExceptionData(ILogger logger, Exception exception, string password);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "erro na etapa {Etapa}")]
    private static partial void LogErrorWithStep(ILogger logger, Exception exception, string etapa);

    /// <summary>Record com segredo usado no caso (c) da SPEC-0018:IT-01.</summary>
    private sealed record ProbeCredential(string Usuario, string Password);

    /// <summary>Objeto com getter que lança (SPEC-0018:IT-02).</summary>
    private sealed class ThrowingGetterAccount(string senha)
    {
        public string Nome => $"conta-comum-{senha.Length}";

        public string Falha => throw new InvalidOperationException($"getter quebrado {senha.Length}");

        public string Senha => senha;
    }

    /// <summary>Sem membros públicos; ToString lança (SPEC-0018:IT-02).</summary>
    private sealed class ThrowingToString(string valor)
    {
        private readonly string _valor = valor;

        public override string ToString() => throw new InvalidOperationException($"ToString quebrado {_valor.Length}");
    }

    /// <summary>Sem membros públicos; ToString devolve null (SPEC-0018:IT-02).</summary>
    private sealed class NullToString(string valor)
    {
        private readonly string _valor = valor;

        public override string ToString() => _valor.Length < 0 ? _valor : null!;
    }

    /// <summary>Valor formatável sem membros públicos, cujo texto é o segredo (SPEC-0018:IT-02).</summary>
    private sealed class ProbeFormattable(string valor) : IFormattable
    {
        private readonly string _valor = valor;

        public string ToString(string? format, IFormatProvider? formatProvider) => _valor;

        public override string ToString() => _valor;
    }

    /// <summary>Nó que aponta para si mesmo (SPEC-0018:IT-02).</summary>
    private sealed class CyclicNode
    {
        public string Nome { get; set; } = string.Empty;

        public string Senha { get; set; } = string.Empty;

        public CyclicNode? Proximo { get; set; }
    }

    /// <summary>Objeto do domínio do escopo (SPEC-0018:IT-02).</summary>
    private sealed record ProbeProfile(string Loja, string Senha);

    /// <summary>Objeto do domínio com endereço de conexão (SPEC-0018:IT-02).</summary>
    private sealed record ProbeConnection(string Nome, Uri Endereco);

    /// <summary>Struct do domínio com senha (SPEC-0018:IT-02).</summary>
    private readonly record struct ProbeStructCredential(string Usuario, string Senha);

    /// <summary>Objeto com mais de 100 membros públicos (SPEC-0018:IT-02).</summary>
    private sealed class WideObject
    {
        public string Senha { get; init; } = string.Empty;

        public string Campo000 { get; init; } = "campo";

        public string Campo001 { get; init; } = "campo";

        public string Campo002 { get; init; } = "campo";

        public string Campo003 { get; init; } = "campo";

        public string Campo004 { get; init; } = "campo";

        public string Campo005 { get; init; } = "campo";

        public string Campo006 { get; init; } = "campo";

        public string Campo007 { get; init; } = "campo";

        public string Campo008 { get; init; } = "campo";

        public string Campo009 { get; init; } = "campo";

        public string Campo010 { get; init; } = "campo";

        public string Campo011 { get; init; } = "campo";

        public string Campo012 { get; init; } = "campo";

        public string Campo013 { get; init; } = "campo";

        public string Campo014 { get; init; } = "campo";

        public string Campo015 { get; init; } = "campo";

        public string Campo016 { get; init; } = "campo";

        public string Campo017 { get; init; } = "campo";

        public string Campo018 { get; init; } = "campo";

        public string Campo019 { get; init; } = "campo";

        public string Campo020 { get; init; } = "campo";

        public string Campo021 { get; init; } = "campo";

        public string Campo022 { get; init; } = "campo";

        public string Campo023 { get; init; } = "campo";

        public string Campo024 { get; init; } = "campo";

        public string Campo025 { get; init; } = "campo";

        public string Campo026 { get; init; } = "campo";

        public string Campo027 { get; init; } = "campo";

        public string Campo028 { get; init; } = "campo";

        public string Campo029 { get; init; } = "campo";

        public string Campo030 { get; init; } = "campo";

        public string Campo031 { get; init; } = "campo";

        public string Campo032 { get; init; } = "campo";

        public string Campo033 { get; init; } = "campo";

        public string Campo034 { get; init; } = "campo";

        public string Campo035 { get; init; } = "campo";

        public string Campo036 { get; init; } = "campo";

        public string Campo037 { get; init; } = "campo";

        public string Campo038 { get; init; } = "campo";

        public string Campo039 { get; init; } = "campo";

        public string Campo040 { get; init; } = "campo";

        public string Campo041 { get; init; } = "campo";

        public string Campo042 { get; init; } = "campo";

        public string Campo043 { get; init; } = "campo";

        public string Campo044 { get; init; } = "campo";

        public string Campo045 { get; init; } = "campo";

        public string Campo046 { get; init; } = "campo";

        public string Campo047 { get; init; } = "campo";

        public string Campo048 { get; init; } = "campo";

        public string Campo049 { get; init; } = "campo";

        public string Campo050 { get; init; } = "campo";

        public string Campo051 { get; init; } = "campo";

        public string Campo052 { get; init; } = "campo";

        public string Campo053 { get; init; } = "campo";

        public string Campo054 { get; init; } = "campo";

        public string Campo055 { get; init; } = "campo";

        public string Campo056 { get; init; } = "campo";

        public string Campo057 { get; init; } = "campo";

        public string Campo058 { get; init; } = "campo";

        public string Campo059 { get; init; } = "campo";

        public string Campo060 { get; init; } = "campo";

        public string Campo061 { get; init; } = "campo";

        public string Campo062 { get; init; } = "campo";

        public string Campo063 { get; init; } = "campo";

        public string Campo064 { get; init; } = "campo";

        public string Campo065 { get; init; } = "campo";

        public string Campo066 { get; init; } = "campo";

        public string Campo067 { get; init; } = "campo";

        public string Campo068 { get; init; } = "campo";

        public string Campo069 { get; init; } = "campo";

        public string Campo070 { get; init; } = "campo";

        public string Campo071 { get; init; } = "campo";

        public string Campo072 { get; init; } = "campo";

        public string Campo073 { get; init; } = "campo";

        public string Campo074 { get; init; } = "campo";

        public string Campo075 { get; init; } = "campo";

        public string Campo076 { get; init; } = "campo";

        public string Campo077 { get; init; } = "campo";

        public string Campo078 { get; init; } = "campo";

        public string Campo079 { get; init; } = "campo";

        public string Campo080 { get; init; } = "campo";

        public string Campo081 { get; init; } = "campo";

        public string Campo082 { get; init; } = "campo";

        public string Campo083 { get; init; } = "campo";

        public string Campo084 { get; init; } = "campo";

        public string Campo085 { get; init; } = "campo";

        public string Campo086 { get; init; } = "campo";

        public string Campo087 { get; init; } = "campo";

        public string Campo088 { get; init; } = "campo";

        public string Campo089 { get; init; } = "campo";

        public string Campo090 { get; init; } = "campo";

        public string Campo091 { get; init; } = "campo";

        public string Campo092 { get; init; } = "campo";

        public string Campo093 { get; init; } = "campo";

        public string Campo094 { get; init; } = "campo";

        public string Campo095 { get; init; } = "campo";

        public string Campo096 { get; init; } = "campo";

        public string Campo097 { get; init; } = "campo";

        public string Campo098 { get; init; } = "campo";

        public string Campo099 { get; init; } = "campo";

        public string Campo100 { get; init; } = "campo";

        public string Campo101 { get; init; } = "campo";

        public string Campo102 { get; init; } = "campo";

        public string Campo103 { get; init; } = "campo";

        public string Campo104 { get; init; } = "campo";

        public string Campo105 { get; init; } = "campo";

        public string Campo106 { get; init; } = "campo";

        public string Campo107 { get; init; } = "campo";

        public string Campo108 { get; init; } = "campo";

        public string Campo109 { get; init; } = "campo";
    }

    /// <summary>Coleção já descartada: a enumeração lança (caso (e) da SPEC-0018:IT-02).</summary>
    private sealed class DisposedEnumerable : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => throw new ObjectDisposedException(nameof(DisposedEnumerable));

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Lê, do item de evento do envelope, os <c>exception.values[].value</c>.</summary>
    private static List<string?> ExceptionValues(string envelope)
    {
        var eventJson = envelope.Split('\n').Single(line => line.Contains("\"exception\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(eventJson);
        return [.. document.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray()
            .Select(value => value.TryGetProperty("value", out var text) ? text.GetString() : null)];
    }

    /// <summary>Lê, do item de evento do envelope, as chaves de <c>exception.values[].mechanism.data</c>.</summary>
    private static Dictionary<string, string?> MechanismData(string envelope)
    {
        var eventJson = envelope.Split('\n').Single(line => line.Contains("\"exception\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(eventJson);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var value in document.RootElement.GetProperty("exception").GetProperty("values").EnumerateArray())
        {
            if (value.TryGetProperty("mechanism", out var mechanism) && mechanism.TryGetProperty("data", out var data))
            {
                foreach (var property in data.EnumerateObject())
                {
                    result[property.Name] = property.Value.ToString();
                }
            }
        }

        return result;
    }

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
