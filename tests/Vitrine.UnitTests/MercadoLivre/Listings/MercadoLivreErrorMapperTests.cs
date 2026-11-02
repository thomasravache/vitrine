using Vitrine.MercadoLivre.Infrastructure.Api;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.UnitTests.MercadoLivre.Listings;

/// <summary>Tradução dos erros de escrita do ML para os códigos estáveis do contrato (SPEC-0033, seção 6).</summary>
public sealed class MercadoLivreErrorMapperTests
{
    [Theory]
    [Trait("spec", "SPEC-0033:UT-04")]
    [InlineData("error-title-not-modifiable.json", "FIELD_NOT_UPDATABLE")]
    [InlineData("error-description-invalid.json", "DESCRIPTION_INVALID")]
    [InlineData("error-item-not-active.json", "ITEM_NOT_ACTIVE")]
    [InlineData("error-conflict.json", "ML_CONFLICT")]
    public void Fixtures_de_erro_do_ML_viram_o_codigo_do_contrato(string fixture, string expected)
    {
        // Dado a fixture de erro do ML (status e corpo { message, error, status, cause })
        var error = MercadoLivreFixtures.Load(fixture);

        // Quando o mapeador a traduz
        var rejected = MercadoLivreErrorMapper.Map(error.GetProperty("status").GetInt32(), error.GetProperty("response").GetRawText());

        // Então o código é o do contrato, com mensagem
        Assert.Equal(expected, rejected.Code);
        Assert.False(string.IsNullOrWhiteSpace(rejected.Message));
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-04")]
    public void Field_not_updatable_que_nao_cita_title_vira_ML_REJECTED()
    {
        // Dado o 400 field_not_updatable de um atributo (cause e mensagem não citam title — emenda v1.1)
        var error = MercadoLivreFixtures.Load("error-field-not-updatable-attribute.json");
        var body = error.GetProperty("response").GetRawText();
        Assert.DoesNotContain("title", body, StringComparison.OrdinalIgnoreCase);

        // Quando traduzido; então é recusa do corpo inteiro (ML_REJECTED), não do título
        Assert.Equal("ML_REJECTED", MercadoLivreErrorMapper.Map(error.GetProperty("status").GetInt32(), body).Code);
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-04")]
    [InlineData("""{"message":"Validation error","error":"validation_error","status":400,"cause":[{"code":"field_not_updatable","references":["item.title"],"message":"title is not modifiable."}]}""")]
    [InlineData("""{"message":"Validation error","error":"validation_error","status":400,"cause":[{"code":"field_not_updatable","message":"Field title cannot be updated."}]}""")]
    public void Field_not_updatable_que_cita_title_vira_FIELD_NOT_UPDATABLE(string body)
    {
        Assert.Equal("FIELD_NOT_UPDATABLE", MercadoLivreErrorMapper.Map(400, body).Code);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-04")]
    public void Status_403_vira_ML_FORBIDDEN()
    {
        const string body = """{"message":"Caller is not authorized to access this resource","error":"forbidden","status":403,"cause":[]}""";

        Assert.Equal("ML_FORBIDDEN", MercadoLivreErrorMapper.Map(403, body).Code);
    }

    [Fact]
    [Trait("spec", "SPEC-0033:UT-04")]
    public void Outro_400_vira_ML_REJECTED_com_a_message_do_ML()
    {
        // Dado um 400 sem causa conhecida
        const string message = "Attribute [WARRANTY_TYPE] has an invalid value";
        var body = $$"""{"message":"{{message}}","error":"validation_error","status":400,"cause":[{"code":"item.attribute.invalid","message":"invalid"}]}""";

        // Quando traduzido
        var rejected = MercadoLivreErrorMapper.Map(400, body);

        // Então é ML_REJECTED com a message do ML
        Assert.Equal("ML_REJECTED", rejected.Code);
        Assert.Equal(message, rejected.Message);
    }

    [Theory]
    [Trait("spec", "SPEC-0033:UT-04")]
    [InlineData("<html><body>Bad Request</body></html>")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("""{"cause": "não é um array""")]
    public void Corpo_ilegivel_vira_ML_REJECTED_com_mensagem_generica(string? body)
    {
        var rejected = MercadoLivreErrorMapper.Map(400, body);

        Assert.Equal("ML_REJECTED", rejected.Code);
        Assert.False(string.IsNullOrWhiteSpace(rejected.Message), "A mensagem genérica não pode ser vazia.");
        Assert.DoesNotContain("<html>", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }
}
