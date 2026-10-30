using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>Linha crua de <c>mercadolivre.notifications</c> (colunas do contrato da SPEC-0023, §6).</summary>
internal sealed record NotificationRow(
    Guid Id,
    string NotificationId,
    string Topic,
    string Resource,
    long MlUserId,
    long ApplicationId,
    int Attempts,
    DateTimeOffset SentAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? ProcessedAt,
    string? Outcome);

/// <summary>
/// Suporte aos testes do webhook de notificações do ML (SPEC-0023): o ML simulado faz POST anônimo (sem cookie nem
/// antiforgery) em <see cref="Path"/>; o payload parte da fixture no formato documentado
/// (<c>Fixtures/notification-items.json</c>) e o inbox é lido direto no PostgreSQL.
/// </summary>
internal static class MercadoLivreNotifications
{
    public const string Path = "/api/v1/mercadolivre/notifications";

    /// <summary>Limite por IP do webhook (requisições por minuto; padrão do contrato: 600).</summary>
    public const string PerIpLimitSetting = "RateLimiting:MercadoLivreNotificationsPerIp:PermitLimit";

    /// <summary>Contador <c>vitrine.mercadolivre.notifications{topic,result}</c> no meter do módulo.</summary>
    public const string Metric = "vitrine.mercadolivre.notifications";

    /// <summary><c>application_id</c> esperado: o <c>MercadoLivre:ClientId</c> do host de teste.</summary>
    public static readonly long ApplicationId = long.Parse(MercadoLivreHost.ClientId, System.Globalization.CultureInfo.InvariantCulture);

    private static long _nextItem = 7_000_000_000;

    /// <summary>A notificação da fixture (formato documentado pelo ML).</summary>
    public static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "MercadoLivre", "Fixtures", "notification-items.json")))!
            ["notification"]!.DeepClone().AsObject();

    /// <summary>Id de anúncio novo (<c>MLB</c> + dígitos).</summary>
    public static string NewItemId() => "MLB" + Interlocked.Increment(ref _nextItem).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Notificação <c>items</c> da fixture para o vendedor <paramref name="mlUserId"/> e o anúncio <paramref name="itemId"/>,
    /// com <c>_id</c> novo, <c>application_id</c> do app e <c>sent</c>/<c>received</c> agora.
    /// </summary>
    public static JsonObject Items(long mlUserId, string itemId)
    {
        var now = DateTimeOffset.UtcNow;
        var notification = Fixture();
        notification["_id"] = NewNotificationId();
        notification["resource"] = "/items/" + itemId;
        notification["user_id"] = mlUserId;
        notification["application_id"] = ApplicationId;
        notification["sent"] = Timestamp(now.AddMilliseconds(-20));
        notification["received"] = Timestamp(now.AddMilliseconds(-30));
        return notification;
    }

    public static string NewNotificationId() => Guid.NewGuid().ToString("D");

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Cliente do ML: sem cookie nem antiforgery, em HTTPS (o Caddy termina o TLS); <paramref name="remoteIp"/> fixa o IP
    /// de origem (sem ele, cada requisição chega de um IP novo — <see cref="TestRemoteIpStartupFilter"/>).
    /// </summary>
    public static HttpClient Client(ApiFactory factory, IPAddress? remoteIp = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return remoteIp is null
            ? factory.CreateDefaultClient(Browser.HttpsOrigin)
            : factory.CreateDefaultClient(Browser.HttpsOrigin, new TestRemoteIpHandler(remoteIp));
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonNode notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return PostRawAsync(client, notification.ToJsonString(), cancellationToken);
    }

    public static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(Path, UriKind.Relative), content, cancellationToken);
    }

    /// <summary>POST e o tempo de resposta medido no cliente (até os cabeçalhos e o corpo lidos).</summary>
    public static async Task<(HttpStatusCode Status, TimeSpan Elapsed)> TimedPostAsync(
        HttpClient client, JsonNode notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(notification);
        var body = notification.ToJsonString();
        var stopwatch = Stopwatch.StartNew();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(Path, UriKind.Relative), content, cancellationToken);
        await response.Content.ReadAsByteArrayAsync(cancellationToken);
        stopwatch.Stop();
        return (response.StatusCode, stopwatch.Elapsed);
    }

    /// <summary>Resposta 200 com corpo vazio (nada do corpo da requisição é refletido).</summary>
    public static async Task AssertEmptyOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Webhook respondeu {(int)response.StatusCode}; esperado 200. Corpo: {body}");
        Assert.Equal(string.Empty, body);
    }

    /// <summary>Linhas do inbox com o <c>_id</c> informado.</summary>
    public static async Task<List<NotificationRow>> ReadAsync(string connectionString, string notificationId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, notification_id::text, topic::text, resource::text, ml_user_id, application_id, attempts,
                   sent_at, received_at, accepted_at, processed_at, outcome::text
            from mercadolivre.notifications
            where notification_id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", notificationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<NotificationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NotificationRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    /// <summary>Total de linhas do inbox.</summary>
    public static async Task<long> CountAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from mercadolivre.notifications", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Espera a linha do <c>_id</c> ser processada (<c>processed_at</c> preenchido) e a devolve.</summary>
    public static async Task<NotificationRow> WaitProcessedAsync(string connectionString, string notificationId, CancellationToken cancellationToken)
    {
        NotificationRow? processed = null;
        await Eventually.TrueAsync(
            async () =>
            {
                processed = (await ReadAsync(connectionString, notificationId, cancellationToken)).SingleOrDefault(row => row.ProcessedAt is not null);
                return processed is not null;
            },
            TimeSpan.FromSeconds(30),
            $"a notificação {notificationId} não foi processada (processed_at) no inbox",
            cancellationToken);
        return processed!;
    }
}
