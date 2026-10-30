using System.Text;
using System.Text.Json.Nodes;
using Vitrine.MercadoLivre.Application.Notifications;

namespace Vitrine.UnitTests.MercadoLivre;

public sealed class NotificationPayloadTests
{
    /// <summary>
    /// Formato documentado da notificação do ML (https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes,
    /// consultado em 2026-10-30), com <c>user_id</c> acima de Int32 e <c>actions</c>.
    /// </summary>
    private const string Documented =
        """
        {
          "_id": "f9f08571-1f65-4c46-9e0a-c0f43faa1557e",
          "resource": "/items/MLB1234567890",
          "user_id": 3000000001,
          "topic": "items",
          "application_id": 2069392825111111,
          "attempts": 1,
          "sent": "2026-10-30T13:58:23.347Z",
          "received": "2026-10-30T13:58:23.329Z",
          "actions": ["item_updated"]
        }
        """;

    public static TheoryData<string, string> MissingFields => new()
    {
        { "_id", "_id" },
        { "resource", "resource" },
        { "user_id", "user_id" },
        { "topic", "topic" },
        { "application_id", "application_id" },
        { "attempts", "attempts" },
        { "sent", "sent" },
        { "received", "received" },
    };

    /// <summary>Campo, valor JSON (cru) de tipo errado ou fora do formato e o campo esperado no erro.</summary>
    public static TheoryData<string, string> WrongValues => new()
    {
        { "user_id", "\"abc\"" },
        { "user_id", "\"3000000001\"" },
        { "user_id", "1.5" },
        { "user_id", "null" },
        { "user_id", "99999999999999999999" },
        { "_id", "\"\"" },
        { "_id", "\"   \"" },
        { "_id", "123" },
        { "_id", "null" },
        { "resource", "42" },
        { "topic", "true" },
        { "application_id", "\"2069392825111111\"" },
        { "attempts", "\"1\"" },
        { "attempts", "1.5" },
        { "sent", "\"ontem\"" },
        { "sent", "\"2026-13-45T99:00:00Z\"" },
        { "sent", "1730296703" },
        { "received", "\"invalida\"" },
    };

    [Fact]
    [Trait("spec", "SPEC-0023:UT-02")]
    public void Payload_documentado_completo_com_user_id_acima_de_Int32_e_aceito()
    {
        // Dado o payload documentado completo; quando validado
        var result = NotificationPayload.Parse(Encoding.UTF8.GetBytes(Documented));

        // Então é aceito, com os campos do inbox (user_id int64, datas em UTC)
        var valid = Assert.IsType<NotificationPayloadResult.Valid>(result);
        var notification = valid.Notification;
        Assert.Equal("f9f08571-1f65-4c46-9e0a-c0f43faa1557e", notification.NotificationId);
        Assert.Equal("/items/MLB1234567890", notification.Resource);
        Assert.Equal(3_000_000_001L, notification.MlUserId);
        Assert.Equal("items", notification.Topic);
        Assert.Equal(2_069_392_825_111_111L, notification.ApplicationId);
        Assert.Equal(1, notification.Attempts);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 58, 23, 347, TimeSpan.Zero), notification.Sent);
        Assert.Equal(TimeSpan.Zero, notification.Sent.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 58, 23, 329, TimeSpan.Zero), notification.Received);
    }

    [Fact]
    [Trait("spec", "SPEC-0023:UT-02")]
    public void Payload_sem_actions_e_com_campos_desconhecidos_e_aceito()
    {
        // Dado o payload sem o opcional actions e com um campo que o contrato não conhece
        var payload = Payload();
        payload.Remove("actions");
        payload["novo_campo_do_ml"] = "qualquer";

        // Quando validado; então é aceito
        Assert.IsType<NotificationPayloadResult.Valid>(Parse(payload));
    }

    [Theory]
    [Trait("spec", "SPEC-0023:UT-02")]
    [MemberData(nameof(MissingFields))]
    public void Payload_sem_campo_obrigatorio_e_recusado_apontando_o_campo(string field, string expectedError)
    {
        // Dado o payload documentado sem o campo obrigatório
        var payload = Payload();
        Assert.True(payload.Remove(field));

        // Quando validado; então é recusado, com erro no campo
        var invalid = Assert.IsType<NotificationPayloadResult.Invalid>(Parse(payload));
        Assert.Contains(expectedError, invalid.Errors.Keys);
    }

    [Theory]
    [Trait("spec", "SPEC-0023:UT-02")]
    [MemberData(nameof(WrongValues))]
    public void Payload_com_campo_de_tipo_errado_ou_fora_do_formato_e_recusado_apontando_o_campo(string field, string rawValue)
    {
        // Dado o payload documentado com o campo trocado por um valor de tipo errado (user_id texto, _id vazio, sent inválido...)
        var payload = Payload();
        payload[field] = JsonNode.Parse(rawValue);

        // Quando validado; então é recusado, com erro no campo
        var invalid = Assert.IsType<NotificationPayloadResult.Invalid>(Parse(payload));
        Assert.Contains(field, invalid.Errors.Keys);
    }

    [Theory]
    [Trait("spec", "SPEC-0023:UT-02")]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("{\"_id\":")]
    public void Corpo_que_nao_e_objeto_JSON_valido_e_recusado(string body)
    {
        // Dado um corpo que não é objeto JSON; quando validado
        var result = NotificationPayload.Parse(Encoding.UTF8.GetBytes(body));

        // Então é recusado (sem exceção), com ao menos um erro
        var invalid = Assert.IsType<NotificationPayloadResult.Invalid>(result);
        Assert.NotEmpty(invalid.Errors);
    }

    private static JsonObject Payload() => JsonNode.Parse(Documented)!.AsObject();

    private static NotificationPayloadResult Parse(JsonObject payload) =>
        NotificationPayload.Parse(Encoding.UTF8.GetBytes(payload.ToJsonString()));
}
