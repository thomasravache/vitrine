using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Observability.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Infrastructure;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Webhook de notificações do ML (SPEC-0023): <c>POST /api/v1/mercadolivre/notifications</c> grava no inbox
/// <c>mercadolivre.notifications</c> (dedupe pelo <c>_id</c>) e responde 200 vazio; o processamento durável localiza a
/// conta pelo <c>user_id</c> e, no tópico <c>items</c>, publica <c>MercadoLivreItemChanged</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationsWebhookTests(PostgresFixture postgres)
{
    /// <summary>Espera curta depois do processamento, para um evento indevido (atrasado pelo outbox) aparecer.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    [Fact]
    [Trait("spec", "SPEC-0023:IT-01")]
    public async Task Notificacao_items_valida_responde_200_vazio_grava_no_inbox_e_publica_MercadoLivreItemChanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        using var metrics = new AuthMetricsListener(factory.Services.GetRequiredService<IMeterFactory>(), MercadoLivreModule.MeterName);

        // Dado uma conta conectada e uma notificação items válida dela (com actions e um campo que o contrato não guarda)
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var itemId = MercadoLivreNotifications.NewItemId();
        var notification = MercadoLivreNotifications.Items(account.Seller.Id, itemId);
        var actionMarker = "acao-marcador-" + Guid.NewGuid().ToString("N");
        var extraMarker = "extra-marcador-" + Guid.NewGuid().ToString("N");
        notification["actions"] = new JsonArray(actionMarker);
        notification["campo_extra"] = extraMarker;
        var notificationId = notification["_id"]!.GetValue<string>();
        var sent = MercadoLivreNotifications.ParseTimestamp(notification["sent"]!.GetValue<string>());
        var received = MercadoLivreNotifications.ParseTimestamp(notification["received"]!.GetValue<string>());

        // Quando o ML a envia
        using var client = MercadoLivreNotifications.Client(factory);
        var before = DateTimeOffset.UtcNow;
        using (var response = await MercadoLivreNotifications.PostAsync(client, notification, cancellationToken))
        {
            // Então responde 200 com corpo vazio
            await MercadoLivreNotifications.AssertEmptyOkAsync(response, cancellationToken);
        }

        var after = DateTimeOffset.UtcNow;

        // E a linha fica no inbox, com os campos da notificação
        var row = Assert.Single(await MercadoLivreNotifications.ReadAsync(connectionString, notificationId, cancellationToken));
        Assert.Equal("items", row.Topic);
        Assert.Equal("/items/" + itemId, row.Resource);
        Assert.Equal(account.Seller.Id, row.MlUserId);
        Assert.Equal(MercadoLivreNotifications.ApplicationId, row.ApplicationId);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(sent, row.SentAt);
        Assert.Equal(received, row.ReceivedAt);
        Assert.InRange(row.AcceptedAt, before.AddSeconds(-1), after.AddSeconds(1));

        // E um assinante de teste recebe MercadoLivreItemChanged com conta, workspace, anúncio e notificação corretos
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ItemChangesFor(notificationId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreItemChanged da notificação",
            cancellationToken);
        var changed = Assert.Single(MercadoLivreEventsTestHandler.ItemChangesFor(notificationId));
        Assert.Equal(account.AccountId, changed.AccountId);
        Assert.Equal(account.WorkspaceId, changed.WorkspaceId);
        Assert.Equal(account.Seller.Id, changed.MlUserId);
        Assert.Equal(itemId, changed.ItemId);
        Assert.Equal(notificationId, changed.NotificationId);
        Assert.Equal(sent, changed.OccurredAt);

        // E a linha é marcada processada, sem desfecho de recusa
        var processed = await MercadoLivreNotifications.WaitProcessedAsync(connectionString, notificationId, cancellationToken);
        Assert.False(string.IsNullOrEmpty(processed.Outcome), "A notificação processada deveria ter outcome.");
        Assert.NotEqual("unknown_user", processed.Outcome);
        Assert.NotEqual("invalid", processed.Outcome);

        // E nada do corpo além dos campos do contrato é gravado
        var dump = await MercadoLivreDatabase.DumpAsync(connectionString, "notifications", cancellationToken);
        Assert.DoesNotContain(actionMarker, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(extraMarker, dump, StringComparison.Ordinal);

        // E a métrica conta a notificação aceita
        Assert.True(metrics.Total(MercadoLivreNotifications.Metric, "accepted") == 1, metrics.Describe());
    }

    [Fact]
    [Trait("spec", "SPEC-0023:IT-02")]
    public async Task Mesma_notificacao_enviada_3_vezes_gera_1_linha_e_1_evento()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        using var metrics = new AuthMetricsListener(factory.Services.GetRequiredService<IMeterFactory>(), MercadoLivreModule.MeterName);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        var notification = MercadoLivreNotifications.Items(account.Seller.Id, MercadoLivreNotifications.NewItemId());
        var notificationId = notification["_id"]!.GetValue<string>();
        using var client = MercadoLivreNotifications.Client(factory);

        // Dado a mesma notificação (mesmo _id) enviada 3 vezes (retentativas do ML: attempts sobe)
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            notification["attempts"] = attempt;
            using var response = await MercadoLivreNotifications.PostAsync(client, notification, cancellationToken);

            // Então as 3 respondem 200
            await MercadoLivreNotifications.AssertEmptyOkAsync(response, cancellationToken);
        }

        // E há 1 linha no inbox (a primeira) e 1 evento publicado
        var row = Assert.Single(await MercadoLivreNotifications.ReadAsync(connectionString, notificationId, cancellationToken));
        Assert.Equal(1, row.Attempts);
        await MercadoLivreNotifications.WaitProcessedAsync(connectionString, notificationId, cancellationToken);
        await Eventually.TrueAsync(
            () => MercadoLivreEventsTestHandler.ItemChangesFor(notificationId).Count > 0,
            TimeSpan.FromSeconds(30),
            "o assinante de teste não recebeu MercadoLivreItemChanged da notificação",
            cancellationToken);
        await Task.Delay(Settle, cancellationToken);
        Assert.Single(MercadoLivreEventsTestHandler.ItemChangesFor(notificationId));
        Assert.Single(await MercadoLivreNotifications.ReadAsync(connectionString, notificationId, cancellationToken));

        // E a métrica conta 1 aceita e 2 duplicadas
        Assert.True(metrics.Total(MercadoLivreNotifications.Metric, "accepted") == 1, metrics.Describe());
        Assert.True(metrics.Total(MercadoLivreNotifications.Metric, "duplicate") == 2, metrics.Describe());
    }

    [Fact]
    [Trait("spec", "SPEC-0023:IT-03")]
    public async Task Outro_app_e_topico_nao_tratado_nao_gravam_e_vendedor_desconhecido_e_resource_invalido_sao_processados_sem_evento()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        using var metrics = new AuthMetricsListener(factory.Services.GetRequiredService<IMeterFactory>(), MercadoLivreModule.MeterName);
        var account = await MercadoLivreGateway.SeedValidAccountAsync(factory, cancellationToken);
        using var client = MercadoLivreNotifications.Client(factory);

        // Dado notificações de outro app, do tópico questions, de um vendedor sem conta e com resource inválido
        var otherApp = MercadoLivreNotifications.Items(account.Seller.Id, MercadoLivreNotifications.NewItemId());
        otherApp["application_id"] = MercadoLivreNotifications.ApplicationId + 1;

        var questions = MercadoLivreNotifications.Items(account.Seller.Id, MercadoLivreNotifications.NewItemId());
        questions["topic"] = "questions";
        questions["resource"] = "/questions/5036111111";

        var unknownUser = MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId());

        var invalidResource = MercadoLivreNotifications.Items(account.Seller.Id, MercadoLivreNotifications.NewItemId());
        invalidResource["resource"] = "/items/abc";

        // Quando o ML as envia, então todas respondem 200 (vazio)
        foreach (var notification in new[] { otherApp, questions, unknownUser, invalidResource })
        {
            using var response = await MercadoLivreNotifications.PostAsync(client, notification, cancellationToken);
            await MercadoLivreNotifications.AssertEmptyOkAsync(response, cancellationToken);
        }

        // E as duas primeiras não gravam nada
        Assert.Empty(await MercadoLivreNotifications.ReadAsync(connectionString, Id(otherApp), cancellationToken));
        Assert.Empty(await MercadoLivreNotifications.ReadAsync(connectionString, Id(questions), cancellationToken));

        // E as duas últimas são processadas como unknown_user e invalid
        Assert.Equal("unknown_user", (await MercadoLivreNotifications.WaitProcessedAsync(connectionString, Id(unknownUser), cancellationToken)).Outcome);
        Assert.Equal("invalid", (await MercadoLivreNotifications.WaitProcessedAsync(connectionString, Id(invalidResource), cancellationToken)).Outcome);

        // E nenhuma delas publica evento
        await Task.Delay(Settle, cancellationToken);
        foreach (var notification in new[] { otherApp, questions, unknownUser, invalidResource })
        {
            Assert.Empty(MercadoLivreEventsTestHandler.ItemChangesFor(Id(notification)));
        }

        Assert.Empty(MercadoLivreEventsTestHandler.ReceivedFor<Vitrine.MercadoLivre.IntegrationEvents.MercadoLivreItemChanged>(account.AccountId));

        // E a métrica conta cada desfecho
        foreach (var result in new[] { "ignored_app", "ignored_topic", "unknown_user", "invalid" })
        {
            Assert.True(metrics.Total(MercadoLivreNotifications.Metric, result) >= 1, $"Sem medição result={result}. {metrics.Describe()}");
        }

        Assert.True(metrics.Total(MercadoLivreNotifications.Metric, "ignored_app") == 1, metrics.Describe());
        Assert.True(metrics.Total(MercadoLivreNotifications.Metric, "ignored_topic") == 1, metrics.Describe());
    }

    [Fact]
    [Trait("spec", "SPEC-0023:IT-04")]
    public async Task JSON_invalido_e_campo_faltando_dao_400_e_corpo_acima_de_16_KB_da_413_sem_refletir_o_corpo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);
        using var client = MercadoLivreNotifications.Client(factory);
        var marker = "marcador-" + Guid.NewGuid().ToString("N");

        // Dado JSON inválido; então 400 VALIDATION_FAILED (problem+json), sem refletir o corpo
        using (var broken = await MercadoLivreNotifications.PostRawAsync(client, "{\"_id\": \"" + marker + "\", \"topic\": ", cancellationToken))
        {
            await AssertValidationFailedAsync(broken, marker, cancellationToken);
        }

        // Dado payload sem user_id; então 400 VALIDATION_FAILED apontando o campo
        var withoutUser = MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId());
        withoutUser["_id"] = marker;
        withoutUser.Remove("user_id");
        using (var missing = await MercadoLivreNotifications.PostAsync(client, withoutUser, cancellationToken))
        {
            var problem = await AssertValidationFailedAsync(missing, marker, cancellationToken);
            Assert.True(
                problem.TryGetProperty("errors", out var errors) && errors.TryGetProperty("user_id", out _),
                "O 400 deveria apontar o campo user_id em errors: " + problem);
        }

        // Dado corpo de 17 KB (com e sem Content-Length); então 413
        var large = Padded(MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId()), 17 * 1024, marker);
        using (var withLength = await MercadoLivreNotifications.PostRawAsync(client, large, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, withLength.StatusCode);
            Assert.DoesNotContain(marker, await withLength.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        using (var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(large))))
        {
            chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(MercadoLivreNotifications.Path, UriKind.Relative)) { Content = chunked };
            request.Headers.TransferEncodingChunked = true;
            using var withoutLength = await client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, withoutLength.StatusCode);
        }

        // E o teto é 16 KB: exatamente 16 KB é aceito
        var limit = Padded(MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId()), 16 * 1024, "x");
        using (var atLimit = await MercadoLivreNotifications.PostRawAsync(client, limit, cancellationToken))
        {
            await MercadoLivreNotifications.AssertEmptyOkAsync(atLimit, cancellationToken);
        }

        // E nada dos recusados foi gravado
        Assert.Empty(await MercadoLivreNotifications.ReadAsync(connectionString, marker, cancellationToken));
    }

    [Fact]
    [Trait("spec", "SPEC-0023:IT-04")]
    public async Task Sem_ClientId_o_webhook_responde_503_ML_NOT_CONFIGURED_e_nao_grava()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado a Api sem MercadoLivre:ClientId
        await using var factory = MercadoLivreHost.Create(connectionString, omit: "ClientId");
        using var client = MercadoLivreNotifications.Client(factory);

        // Quando chega uma notificação válida
        var notification = MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId());
        using var response = await MercadoLivreNotifications.PostAsync(client, notification, cancellationToken);

        // Então 503 ML_NOT_CONFIGURED e nada gravado
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ML_NOT_CONFIGURED", (await ProblemAsync(response, cancellationToken)).GetProperty("code").GetString());
        Assert.Equal(0, await MercadoLivreNotifications.CountAsync(connectionString, cancellationToken));
    }

    private static string Id(JsonObject notification) => notification["_id"]!.GetValue<string>();

    /// <summary>A notificação com um campo extra de enchimento até exatamente <paramref name="totalBytes"/> bytes UTF-8.</summary>
    private static string Padded(JsonObject notification, int totalBytes, string filler)
    {
        notification["enchimento"] = string.Empty;
        var baseLength = Encoding.UTF8.GetByteCount(notification.ToJsonString());
        var padding = new StringBuilder();
        while (padding.Length < totalBytes - baseLength)
        {
            padding.Append(filler);
        }

        padding.Length = totalBytes - baseLength;
        notification["enchimento"] = padding.ToString();
        var json = notification.ToJsonString();
        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(json));
        return json;
    }

    private static async Task<JsonElement> AssertValidationFailedAsync(HttpResponseMessage response, string marker, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Esperado 400; veio {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("code").GetString());
        return problem;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }
}
