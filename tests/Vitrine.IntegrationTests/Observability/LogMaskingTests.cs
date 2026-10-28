using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Observability;

/// <summary>
/// IT-03 (SPEC-0007): nada de e-mail ou senha nos logs que saem da Api. Os registros são capturados em memória no
/// fim do pipeline de logs do OpenTelemetry da aplicação (exportador acrescentado depois dos padrões de serviço),
/// isto é, o que o OTLP/dashboard receberia.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed partial class LogMaskingTests(PostgresFixture postgres)
{
    private const string WrongPassword = "senha-errada-IT03-2026";

    [Fact]
    [Trait("spec", "SPEC-0007:IT-03")]
    public async Task Login_falho_nao_deixa_email_nem_senha_em_nenhum_registro_emitido()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado a Api com os logs capturados em memória e um usuário existente
        var captured = new CapturedLogs();
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = IdentityHost.Create(connectionString)
            .WithServices(services => services.AddOpenTelemetry().WithLogging(logging =>
                logging.AddProcessor(new SimpleLogRecordExportProcessor(captured))));
        var user = TestUser.New();
        using (var registering = new IdentityApi(factory))
        {
            await registering.RegisterSuccessfullyAsync(user, cancellationToken);
        }

        // Quando um login com e-mail e senha falha
        using var api = new IdentityApi(factory);
        using (var failed = await api.LoginAsync(user.Email, WrongPassword, cancellationToken))
        {
            await IdentityApi.AssertProblemAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS", failed, cancellationToken);
        }

        // E algum código da aplicação registra a tentativa com as credenciais (descuido que o enriquecedor cobre)
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vitrine.Identity.Login");
        LogFailedAttempt(logger, user.Email, WrongPassword);

        // Então nenhum registro emitido contém o e-mail nem a senha (mensagem formatada, atributos, escopos, exceção)
        var records = captured.Records;
        Assert.Contains(records, record => record.Category == "Vitrine.Identity.Login");
        foreach (var record in records)
        {
            var text = record.AllText();
            Assert.False(text.Contains(user.Email, StringComparison.OrdinalIgnoreCase), $"E-mail apareceu em log emitido:\n{text}");
            Assert.False(text.Contains(WrongPassword, StringComparison.Ordinal), $"Senha apareceu em log emitido:\n{text}");
            Assert.False(text.Contains(user.Password, StringComparison.Ordinal), $"Senha apareceu em log emitido:\n{text}");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Login falhou para {email} com a senha {password}")]
    private static partial void LogFailedAttempt(ILogger logger, string email, string password);
}
