using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Vitrine.UnitTests.Observability;

/// <summary>
/// UT-01 (SPEC-0007): o enriquecedor de mascaramento faz parte dos padrões de serviço (<c>AddServiceDefaults</c>,
/// usado por todo host do Vitrine). O teste monta só o pipeline de logs do OpenTelemetry em memória — sem rede,
/// banco nem Kestrel — e lê o que sai dele por um exportador acrescentado depois dos padrões, como faria o OTLP.
/// </summary>
public sealed partial class SensitiveDataMaskingTests
{
    private const string Mask = "***";

    private const string Email = "marina.souza@example.com";
    private const string Password = "senha-forte-2026";
    private const string AccessToken = "APP_USR-1234567890-token-de-acesso";
    private const string Authorization = "Bearer eyJhbGciOiJIUzI1NiJ9.segredo.assinatura";
    private const string Cookie = "vitrine.session=CfDJ8sessao-secreta; vitrine.antiforgery=CfDJ8anti";
    private const string UserId = "4f8e2c1a-9b7d-4e3f-8a6b-1c2d3e4f5a6b";
    private const string Result = "failure";

    [Fact]
    [Trait("spec", "SPEC-0007:UT-01")]
    public void Mascara_email_password_access_token_Authorization_e_Cookie_e_mantem_os_demais_campos()
    {
        // Dado o pipeline de logs dos padrões de serviço (com o enriquecedor de mascaramento)
        var captured = new CapturedLogs();
        using (var host = BuildHost(captured))
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vitrine.Testes.Mascaramento");

            // Quando um registro recebe propriedades com e-mail, password, access_token, Authorization e Cookie
            LogWithSensitiveProperties(logger, Email, Password, AccessToken, Authorization, Cookie, UserId, Result);
        }

        // Então os valores sensíveis saem mascarados (***)
        var record = Assert.Single(captured.Records, log => log.Category == "Vitrine.Testes.Mascaramento");
        Assert.Equal(Mask, record.Attribute("email"));
        Assert.Equal(Mask, record.Attribute("password"));
        Assert.Equal(Mask, record.Attribute("access_token"));
        Assert.Equal(Mask, record.Attribute("Authorization"));
        Assert.Equal(Mask, record.Attribute("Cookie"));

        // E os demais campos são mantidos como vieram
        Assert.Equal(UserId, record.Attribute("userId"));
        Assert.Equal(Result, record.Attribute("result"));

        // E nenhum valor sensível sobra em lugar nenhum do registro (inclusive na mensagem formatada)
        AssertContainsNone(record, Email, Password, AccessToken, Authorization, Cookie, "eyJhbGciOiJIUzI1NiJ9", "CfDJ8sessao-secreta");
        Assert.NotNull(record.FormattedMessage);
        Assert.Contains(UserId, record.FormattedMessage, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0007:UT-01")]
    public void Mascara_e_mail_mesmo_em_propriedade_de_nome_neutro()
    {
        // Dado o pipeline de logs dos padrões de serviço
        var captured = new CapturedLogs();
        using (var host = BuildHost(captured))
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vitrine.Testes.Mascaramento");

            // Quando um e-mail chega numa propriedade cujo nome não denuncia o dado (ex.: "destinatario")
            LogWithNeutralProperty(logger, Email, UserId);
        }

        // Então o e-mail não aparece no registro emitido, e o restante é mantido
        var record = Assert.Single(captured.Records, log => log.Category == "Vitrine.Testes.Mascaramento");
        AssertContainsNone(record, Email);
        Assert.Equal(UserId, record.Attribute("userId"));
    }

    private static IHost BuildHost(CapturedLogs captured)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Vitrine.UnitTests",
            EnvironmentName = Environments.Production,
            DisableDefaults = true,
        });

        builder.AddServiceDefaults();

        // Exportador do teste registrado depois dos padrões: recebe o registro já enriquecido, como o OTLP.
        builder.Services.AddOpenTelemetry().WithLogging(logging =>
            logging.AddProcessor(new SimpleLogRecordExportProcessor(captured)));

        return builder.Build();
    }

    private static void AssertContainsNone(CapturedLog record, params string[] secrets)
    {
        var text = record.AllText();
        foreach (var secret in secrets)
        {
            Assert.False(
                text.Contains(secret, StringComparison.OrdinalIgnoreCase),
                $"Valor sensível \"{secret}\" apareceu no log emitido:\n{text}");
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Login {result} para {email} senha {password} token {access_token} Authorization {Authorization} Cookie {Cookie} usuario {userId}")]
    private static partial void LogWithSensitiveProperties(
        ILogger logger,
        string email,
        string password,
        string access_token,
        string Authorization,
        string Cookie,
        string userId,
        string result);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Convite enviado a {destinatario} pelo usuario {userId}")]
    private static partial void LogWithNeutralProperty(ILogger logger, string destinatario, string userId);
}
