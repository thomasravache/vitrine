using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Wolverine;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// SPEC-0023:IT-08 (emenda v1.1) — retenção do inbox <c>mercadolivre.notifications</c>: linhas processadas há mais de
/// 7 dias são apagadas pelo job diário do módulo. O job é a mensagem recorrente <c>CleanupNotificationsInbox</c> do
/// Wolverine (ADR-0014, como <c>CleanupPendingAuthorizations</c> da SPEC-0020); o teste a dispara pelo
/// <see cref="IMessageBus"/>, sem esperar o agendamento.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationsInboxCleanupTests(PostgresFixture postgres)
{
    private const string CleanupMessageName = "CleanupNotificationsInbox";

    [Fact]
    [Trait("spec", "SPEC-0023:IT-08")]
    public async Task Limpeza_apaga_so_as_notificacoes_processadas_ha_mais_de_7_dias()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var message = CreateCleanupMessage();
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        _ = factory.Services; // sobe o host: schema mercadolivre migrado

        // Dado linhas do inbox processadas há 8 dias, há 1 dia e uma não processada antiga (recebida há 30 dias)
        var now = DateTimeOffset.UtcNow;
        var processedLongAgo = await InsertAsync(connectionString, now.AddDays(-8).AddMinutes(-1), now.AddDays(-8), cancellationToken);
        var processedYesterday = await InsertAsync(connectionString, now.AddDays(-1).AddMinutes(-1), now.AddDays(-1), cancellationToken);
        var neverProcessed = await InsertAsync(connectionString, now.AddDays(-30), processedAt: null, cancellationToken);

        // Quando a limpeza diária do módulo roda
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, cancellationToken, TimeSpan.FromSeconds(30));
        }

        // Então só a processada há 8 dias é apagada
        Assert.Empty(await MercadoLivreNotifications.ReadAsync(connectionString, processedLongAgo, cancellationToken));
        Assert.Single(await MercadoLivreNotifications.ReadAsync(connectionString, processedYesterday, cancellationToken));
        Assert.Single(await MercadoLivreNotifications.ReadAsync(connectionString, neverProcessed, cancellationToken));
        Assert.Equal(2, await MercadoLivreNotifications.CountAsync(connectionString, cancellationToken));
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

    /// <summary>Linha do inbox aceita em <paramref name="acceptedAt"/> e processada em <paramref name="processedAt"/> (ou não).</summary>
    private static async Task<string> InsertAsync(
        string connectionString, DateTimeOffset acceptedAt, DateTimeOffset? processedAt, CancellationToken cancellationToken)
    {
        var notificationId = MercadoLivreNotifications.NewNotificationId();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // outcome literal (texto ou enum do PostgreSQL): item_changed nas processadas, nulo na pendente.
#pragma warning disable CA2100 // Só dois literais fixos do teste entram no SQL; os valores vão como parâmetros.
        await using var command = new NpgsqlCommand(
            $"""
            insert into mercadolivre.notifications
                (id, notification_id, topic, resource, ml_user_id, application_id, attempts, sent_at, received_at, accepted_at, processed_at, outcome)
            values (@id, @notification, 'items', @resource, @user, @application, 1, @accepted, @accepted, @accepted, @processed,
                    {(processedAt is null ? "null" : "'item_changed'")})
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
