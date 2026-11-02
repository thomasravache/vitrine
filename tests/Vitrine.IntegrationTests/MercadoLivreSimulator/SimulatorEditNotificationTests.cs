using System.Net;
using System.Text.Json;
using Vitrine.IntegrationTests.MercadoLivreSimulator.Support;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Vitrine.IntegrationTests.MercadoLivreSimulator;

/// <summary>
/// SPEC-0034:IT-04 — toda escrita aceita (item ou descrição) envia a notificação <c>topic=items</c> a
/// <c>Simulator:NotificationUrl</c>; escrita recusada não notifica; receptor fora do ar não muda a resposta.
/// </summary>
public sealed class SimulatorEditNotificationTests
{
    private const string ReceiverPath = "/api/v1/mercadolivre/notifications";

    private static List<JsonElement> Notifications(WireMockServer receiver) =>
        [.. receiver.LogEntries
            .Where(e => e.RequestMessage?.Path == ReceiverPath && e.RequestMessage.Method == "POST")
            .Select(e => JsonDocument.Parse(e.RequestMessage!.Body!).RootElement.Clone())];

    /// <summary>Espera (até 10 s, em passos curtos) o receptor ter ao menos <paramref name="count"/> notificações.</summary>
    private static async Task<List<JsonElement>> WaitForAsync(WireMockServer receiver, int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Notifications(receiver).Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken);
        }

        return Notifications(receiver);
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-04")]
    public async Task PUT_do_item_e_da_descricao_aceitos_notificam_e_escrita_recusada_nao()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado um WireMock como receptor em Simulator:NotificationUrl
        using var receiver = WireMockServer.Start();
        receiver.Given(Request.Create().WithPath(ReceiverPath).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        await using var host = new SimulatorHost(receiver.Url + ReceiverPath);
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var editable = seller.Active[2];
        var withSales = seller.Active[0];

        // Quando uma escrita é recusada (título com vendas)
        var rejected = await client.SendJsonAsync(HttpMethod.Put, $"/items/{withSales}", new { title = "Título recusado" }, seller.AccessToken, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);

        // E um PUT /items/{id} e um PUT da descrição são aceitos
        var item = await client.SendJsonAsync(HttpMethod.Put, $"/items/{editable}", new { title = "Título aceito" }, seller.AccessToken, cancellationToken);
        Assert.True(item.Status == HttpStatusCode.OK, $"PUT /items/{editable} respondeu {(int)item.Status}: {item.Body}");
        var descriptionPath = $"/items/{editable}/description";
        var hasDescription = (await client.GetAsync(descriptionPath, cancellationToken: cancellationToken)).Status == HttpStatusCode.OK;
        var description = hasDescription
            ? await client.SendJsonAsync(HttpMethod.Put, descriptionPath + "?api_version=2", new { plain_text = "Descrição aceita." }, seller.AccessToken, cancellationToken)
            : await client.SendJsonAsync(HttpMethod.Post, descriptionPath, new { plain_text = "Descrição aceita." }, seller.AccessToken, cancellationToken);
        Assert.True(
            description.Status is HttpStatusCode.OK or HttpStatusCode.Created,
            $"Escrita da descrição respondeu {(int)description.Status}: {description.Body}");

        // Então o receptor recebe 2 POSTs topic=items, resource=/items/{id}, com o user_id do vendedor
        await WaitForAsync(receiver, 2, cancellationToken);
        await Task.Delay(300, cancellationToken); // margem para uma notificação indevida (da escrita recusada) chegar
        var notifications = Notifications(receiver);
        Assert.True(notifications.Count == 2, $"Esperava 2 notificações; vieram {notifications.Count}: {string.Join(" | ", notifications)}");
        Assert.All(notifications, notification =>
        {
            Assert.Equal("items", notification.GetProperty("topic").GetString());
            Assert.Equal($"/items/{editable}", notification.GetProperty("resource").GetString());
            Assert.Equal(seller.Id, notification.GetProperty("user_id").GetInt64());
        });
        Assert.NotEqual(notifications[0].GetProperty("_id").GetString(), notifications[1].GetProperty("_id").GetString());
    }

    [Fact]
    [Trait("spec", "SPEC-0034:IT-04")]
    public async Task Receptor_fora_do_ar_nao_muda_a_resposta_da_escrita()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado Simulator:NotificationUrl apontando para um receptor que já foi desligado
        string url;
        using (var stopped = WireMockServer.Start())
        {
            url = stopped.Url + ReceiverPath;
            stopped.Stop();
        }

        await using var host = new SimulatorHost(url);
        using var client = host.NewClient();
        var seller = await client.CreateConnectedSellerAsync(cancellationToken);
        var id = seller.Active[2];

        // Quando o PUT é aceito
        var response = await client.SendJsonAsync(HttpMethod.Put, $"/items/{id}", new { title = "Título com receptor fora" }, seller.AccessToken, cancellationToken);

        // Então a resposta é 200 e a edição vale
        Assert.True(response.Status == HttpStatusCode.OK, $"PUT /items/{id} respondeu {(int)response.Status}: {response.Body}");
        var item = await client.GetAsync($"/items/{id}", cancellationToken: cancellationToken);
        Assert.Equal("Título com receptor fora", item.Json.GetProperty("title").GetString());
    }
}
