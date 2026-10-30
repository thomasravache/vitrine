using System.Globalization;
using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>SPEC-0024:IT-05 — <c>POST /__sim/notify</c> entrega a notificação no formato do ML a <c>Simulator:NotificationUrl</c>.</summary>
public sealed class SimulatorNotificationTests
{
    private const string ReceiverPath = "/api/v1/mercadolivre/notifications";

    [Fact]
    [Trait("spec", "SPEC-0024:IT-05")]
    public async Task Notify_entrega_POST_JSON_no_formato_do_ML_ao_receptor_configurado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um WireMock como receptor em Simulator:NotificationUrl
        using var receiver = WireMockServer.Start();
        receiver.Given(Request.Create().WithPath(ReceiverPath).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        await using var host = new SimulatorHost(receiver.Url + ReceiverPath);
        using var client = host.NewClient();
        const string itemId = "MLB9000000001";

        // Quando o teste pede POST /__sim/notify { userId, itemId }
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var response = await client.PostJsonAsync("/__sim/notify", new { userId = SimulatorHost.SellerTeste, itemId }, cancellationToken);

        // Então responde 202 com o _id gerado e o status entregue pelo receptor
        Assert.True(response.Status == HttpStatusCode.Accepted, $"/__sim/notify respondeu {(int)response.Status}: {response.Body}");
        var result = response.Json;
        var notificationId = result.GetProperty("notificationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(notificationId));
        Assert.Equal(200, result.GetProperty("deliveredStatus").GetInt32());

        // E o receptor recebeu um único POST JSON no formato do ML
        var call = Assert.Single(receiver.LogEntries, e => e.RequestMessage?.Path == ReceiverPath);
        Assert.Equal("POST", call.RequestMessage!.Method);
        Assert.StartsWith("application/json", call.RequestMessage.Headers!["Content-Type"].ToString(), StringComparison.OrdinalIgnoreCase);
        var body = JsonDocument.Parse(call.RequestMessage.Body!).RootElement;
        Assert.Equal(notificationId, body.GetProperty("_id").GetString());
        Assert.Equal($"/items/{itemId}", body.GetProperty("resource").GetString());
        Assert.Equal(SimulatorHost.SellerTeste, body.GetProperty("user_id").GetInt64());
        Assert.Equal("items", body.GetProperty("topic").GetString());
        Assert.Equal(long.Parse(SimulatorHost.ClientId, CultureInfo.InvariantCulture), body.GetProperty("application_id").GetInt64());
        Assert.Equal(1, body.GetProperty("attempts").GetInt32());
        var sent = DateTimeOffset.Parse(body.GetProperty("sent").GetString()!, CultureInfo.InvariantCulture);
        var received = DateTimeOffset.Parse(body.GetProperty("received").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(sent >= before && received >= before, $"sent={sent:o} received={received:o} anteriores ao teste.");
    }

    [Fact]
    [Trait("spec", "SPEC-0024:IT-05")]
    public async Task Cada_notify_gera_um_id_novo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var receiver = WireMockServer.Start();
        receiver.Given(Request.Create().WithPath(ReceiverPath).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        await using var host = new SimulatorHost(receiver.Url + ReceiverPath);
        using var client = host.NewClient();

        // Quando o mesmo anúncio é notificado duas vezes
        var first = await client.PostJsonAsync("/__sim/notify", new { userId = SimulatorHost.SellerTeste, itemId = "MLB9000000002" }, cancellationToken);
        var second = await client.PostJsonAsync("/__sim/notify", new { userId = SimulatorHost.SellerTeste, itemId = "MLB9000000002" }, cancellationToken);

        // Então cada uma tem _id próprio (o Vitrine deduplica por _id)
        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal(HttpStatusCode.Accepted, second.Status);
        Assert.NotEqual(first.Json.GetProperty("notificationId").GetString(), second.Json.GetProperty("notificationId").GetString());
        var ids = receiver.LogEntries
            .Select(e => JsonDocument.Parse(e.RequestMessage!.Body!).RootElement.GetProperty("_id").GetString())
            .ToList();
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
