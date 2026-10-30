using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Wolverine;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Limpeza das autorizações pendentes (contrato da SPEC-0020: "linhas expiradas há mais de 1 dia são apagadas pelo job
/// diário do módulo"). O job é a mensagem <c>CleanupPendingAuthorizations</c> tratada por um handler do Wolverine
/// (agendada — ADR-0014); o teste a dispara pelo <see cref="IMessageBus"/>, sem esperar o agendamento diário.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PendingAuthorizationsCleanupTests(PostgresFixture postgres)
{
    private const string CleanupMessageName = "CleanupPendingAuthorizations";

    [Fact]
    [Trait("spec", "SPEC-0020:IT-03")]
    public async Task Limpeza_apaga_autorizacoes_expiradas_ha_mais_de_1_dia_e_mantem_as_recentes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        _ = factory.Services; // sobe o host: schema mercadolivre migrado

        // Dado autorizações pendentes expiradas há 2 dias (usada e não usada), há 1 hora e ainda válida
        var now = DateTimeOffset.UtcNow;
        var oldUnused = await InsertAsync(connectionString, now.AddDays(-2), usedAt: null, cancellationToken);
        var oldUsed = await InsertAsync(connectionString, now.AddDays(-2), usedAt: now.AddDays(-2).AddMinutes(-5), cancellationToken);
        var recentlyExpired = await InsertAsync(connectionString, now.AddHours(-1), usedAt: null, cancellationToken);
        var valid = await InsertAsync(connectionString, now.AddMinutes(5), usedAt: null, cancellationToken);

        // Quando a mensagem de limpeza do módulo é tratada (o que o agendamento diário dispara)
        var message = CreateCleanupMessage();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, cancellationToken, TimeSpan.FromSeconds(30));
        }

        // Então só as expiradas há mais de 1 dia somem; a expirada há pouco e a válida ficam
        var remaining = (await MercadoLivreDatabase.ReadPendingAuthorizationsAsync(connectionString, cancellationToken)).Select(row => row.Id).ToHashSet();
        Assert.DoesNotContain(oldUnused, remaining);
        Assert.DoesNotContain(oldUsed, remaining);
        Assert.Contains(recentlyExpired, remaining);
        Assert.Contains(valid, remaining);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:IT-03")]
    public async Task Limpeza_esta_agendada_no_Wolverine_do_host_todo_dia_as_03h_UTC()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);

        // Dado o host real; quando as mensagens recorrentes registradas no Wolverine são lidas
        var schedules = factory.Services.GetRequiredService<WolverineOptions>().Schedules;
        var cleanup = schedules.FindByName(CleanupMessageName);

        // Então a limpeza do módulo está agendada com o cron diário do contrato (03:00 UTC)
        Assert.True(cleanup is not null, $"{CleanupMessageName} não está agendada nas WolverineOptions do host.");
        Assert.Equal(CleanupMessageName, cleanup.MessageType.Name);
        Assert.Equal("0 3 * * *", cleanup.Schedule.Expression);
        Assert.Equal(TimeZoneInfo.Utc, cleanup.Schedule.TimeZone);
    }

    /// <summary>
    /// Instância da mensagem <c>CleanupPendingAuthorizations</c> de um assembly do módulo, achada por nome para este
    /// projeto compilar antes de o tipo existir (o teste falha com a mensagem abaixo enquanto ele não existe).
    /// </summary>
    private static object CreateCleanupMessage()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Vitrine.MercadoLivre", StringComparison.Ordinal) == true)
            .SelectMany(assembly => assembly.GetTypes())
            .FirstOrDefault(candidate => candidate.Name == CleanupMessageName);
        Assert.True(
            type is not null,
            $"Mensagem {CleanupMessageName} não existe nos assemblies Vitrine.MercadoLivre.* (o job diário deve ser uma mensagem "
            + "agendada tratada por um handler do Wolverine — ADR-0014).");
        return Activator.CreateInstance(type!)
            ?? throw new InvalidOperationException($"{CleanupMessageName} precisa de construtor sem parâmetros.");
    }

    private static async Task<Guid> InsertAsync(string connectionString, DateTimeOffset expiresAt, DateTimeOffset? usedAt, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into mercadolivre.pending_authorizations (id, workspace_id, user_id, state_hash, code_verifier, expires_at, used_at)
            values (@id, @workspace, @user, @hash, 'verifier-cifrado-de-teste', @expires, @used)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("workspace", Guid.NewGuid());
        command.Parameters.AddWithValue("user", Guid.NewGuid());
        command.Parameters.AddWithValue("hash", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("expires", expiresAt);
        command.Parameters.AddWithValue("used", (object?)usedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }
}
