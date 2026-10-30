using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Wolverine;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// SPEC-0023:IT-08 (emendas v1.1 e v1.2) — retenção do inbox <c>mercadolivre.notifications</c>, apagada pelo job diário
/// do módulo: processadas com sucesso há mais de 7 dias, <c>unknown_user</c>/<c>invalid</c> há mais de 1 dia e não
/// processadas (retentativas esgotadas) recebidas há mais de 7 dias. O job é a mensagem recorrente <c>CleanupNotificationsInbox</c> do
/// Wolverine (ADR-0014, como <c>CleanupPendingAuthorizations</c> da SPEC-0020); o teste a dispara pelo
/// <see cref="IMessageBus"/>, sem esperar o agendamento.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationsInboxCleanupTests(PostgresFixture postgres)
{
    private const string CleanupMessageName = "CleanupNotificationsInbox";

    [Fact]
    [Trait("spec", "SPEC-0023:IT-08")]
    public async Task Limpeza_apaga_sucesso_apos_7_dias_recusadas_apos_1_dia_e_nao_processadas_apos_7_dias()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        _ = factory.Services; // sobe o host: schema mercadolivre migrado e assemblies do módulo carregados
        var message = CreateCleanupMessage();

        // Dado linhas do inbox (emenda v1.2): processadas com sucesso há 8 dias e há 1 dia; processadas como
        // unknown_user/invalid há 2 dias e há 1 hora; não processadas (retentativas esgotadas) recebidas há 8 dias e há 1 dia
        var now = DateTimeOffset.UtcNow;
        var successOld = await InsertAsync(connectionString, now.AddDays(-8).AddMinutes(-1), now.AddDays(-8), "item_changed", cancellationToken);
        var successRecent = await InsertAsync(connectionString, now.AddDays(-1).AddMinutes(-1), now.AddDays(-1), "item_changed", cancellationToken);
        var unknownOld = await InsertAsync(connectionString, now.AddDays(-2).AddMinutes(-1), now.AddDays(-2), "unknown_user", cancellationToken);
        var invalidOld = await InsertAsync(connectionString, now.AddDays(-2).AddMinutes(-1), now.AddDays(-2), "invalid", cancellationToken);
        var unknownRecent = await InsertAsync(connectionString, now.AddHours(-1).AddMinutes(-1), now.AddHours(-1), "unknown_user", cancellationToken);
        var invalidRecent = await InsertAsync(connectionString, now.AddHours(-1).AddMinutes(-1), now.AddHours(-1), "invalid", cancellationToken);
        var pendingOld = await InsertAsync(connectionString, now.AddDays(-8), processedAt: null, outcome: null, cancellationToken);
        var pendingRecent = await InsertAsync(connectionString, now.AddDays(-1), processedAt: null, outcome: null, cancellationToken);

        // Quando a limpeza diária do módulo roda
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, cancellationToken, TimeSpan.FromSeconds(30));
        }

        // Então são apagadas a de sucesso de 8 dias, as unknown_user/invalid de 2 dias e a não processada de 8 dias
        foreach (var (name, id) in new[] { ("sucesso de 8 dias", successOld), ("unknown_user de 2 dias", unknownOld), ("invalid de 2 dias", invalidOld), ("não processada de 8 dias", pendingOld) })
        {
            Assert.True((await MercadoLivreNotifications.ReadAsync(connectionString, id, cancellationToken)).Count == 0, $"A linha {name} deveria ter sido apagada.");
        }

        // E as demais ficam
        foreach (var (name, id) in new[] { ("sucesso de 1 dia", successRecent), ("unknown_user de 1 hora", unknownRecent), ("invalid de 1 hora", invalidRecent), ("não processada de 1 dia", pendingRecent) })
        {
            Assert.True((await MercadoLivreNotifications.ReadAsync(connectionString, id, cancellationToken)).Count == 1, $"A linha {name} deveria ficar.");
        }

        Assert.Equal(4, await MercadoLivreNotifications.CountAsync(connectionString, cancellationToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0023:IT-08")]
    public async Task Limpeza_do_inbox_esta_agendada_no_Wolverine_do_host_todo_dia_em_UTC()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);

        // Dado o host real; quando as mensagens recorrentes registradas no Wolverine são lidas
        var cleanup = factory.Services.GetRequiredService<WolverineOptions>().Schedules.FindByName(CleanupMessageName);

        // Então a limpeza do inbox está agendada uma vez por dia, no cron diário do módulo (03:00 UTC)
        Assert.True(cleanup is not null, $"{CleanupMessageName} não está agendada nas WolverineOptions do host.");
        Assert.Equal(CleanupMessageName, cleanup.MessageType.Name);
        Assert.Equal("0 3 * * *", cleanup.Schedule.Expression);
        Assert.Equal(TimeZoneInfo.Utc, cleanup.Schedule.TimeZone);
    }

    /// <summary>
    /// Instância de <c>CleanupNotificationsInbox</c> de um assembly do módulo, achada por nome para este projeto compilar
    /// antes de o tipo existir (o teste falha com a mensagem abaixo enquanto ele não existe).
    /// </summary>
    private static object CreateCleanupMessage()
    {
        // O assembly de Infrastructure é referenciado pelo projeto de teste: carrega-o antes de procurar (o host já o carrega).
        _ = typeof(Vitrine.MercadoLivre.Infrastructure.MercadoLivreModule).Assembly;
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Vitrine.MercadoLivre", StringComparison.Ordinal) == true)
            .SelectMany(assembly => assembly.GetTypes())
            .FirstOrDefault(candidate => candidate.Name == CleanupMessageName);
        Assert.True(
            type is not null,
            $"Mensagem {CleanupMessageName} não existe nos assemblies Vitrine.MercadoLivre.* (a limpeza diária do inbox deve ser "
            + "uma mensagem agendada tratada por um handler do Wolverine — ADR-0014).");
        return Activator.CreateInstance(type!)
            ?? throw new InvalidOperationException($"{CleanupMessageName} precisa de construtor sem parâmetros.");
    }

    /// <summary>
    /// Linha do inbox recebida e aceita em <paramref name="acceptedAt"/> (<c>received_at</c> = <c>accepted_at</c>) e
    /// processada em <paramref name="processedAt"/> com <paramref name="outcome"/> (ou não processada).
    /// </summary>
    private static async Task<string> InsertAsync(
        string connectionString, DateTimeOffset acceptedAt, DateTimeOffset? processedAt, string? outcome, CancellationToken cancellationToken)
    {
        var notificationId = MercadoLivreNotifications.NewNotificationId();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // outcome literal (texto ou enum do PostgreSQL), só dos valores fixos do contrato.
        Assert.True(outcome is null or "item_changed" or "unknown_user" or "invalid", $"outcome fora do contrato: {outcome}");
#pragma warning disable CA2100 // Só literais fixos do teste (conferidos acima) entram no SQL; os valores vão como parâmetros.
        await using var command = new NpgsqlCommand(
            $"""
            insert into mercadolivre.notifications
                (id, notification_id, topic, resource, ml_user_id, application_id, attempts, sent_at, received_at, accepted_at, processed_at, outcome)
            values (@id, @notification, 'items', @resource, @user, @application, 1, @accepted, @accepted, @accepted, @processed,
                    {(outcome is null ? "null" : $"'{outcome}'")})
            """,
            connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("notification", notificationId);
        command.Parameters.AddWithValue("resource", "/items/" + MercadoLivreNotifications.NewItemId());
        command.Parameters.AddWithValue("user", MlSeller.New().Id);
        command.Parameters.AddWithValue("application", MercadoLivreNotifications.ApplicationId);
        command.Parameters.AddWithValue("accepted", acceptedAt);
        command.Parameters.AddWithValue("processed", (object?)processedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return notificationId;
    }
}
