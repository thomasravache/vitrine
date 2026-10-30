using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Vitrine.MercadoLivre.Infrastructure;

namespace Vitrine.UnitTests.MercadoLivre;

public sealed class MercadoLivreOptionsValidatorTests
{
    private const string NonOfficialApi = "https://ml-simulador.vitrine.test";
    private const string NonOfficialAuth = "https://ml-simulador.vitrine.test/auth";

    [Theory]
    [Trait("spec", "SPEC-0020:UT-04")]
    [InlineData(NonOfficialAuth, MercadoLivreOptions.OfficialApiBaseUrl)]
    [InlineData(MercadoLivreOptions.OfficialAuthorizationBaseUrl, NonOfficialApi)]
    [InlineData("http://auth.mercadolivre.com.br", MercadoLivreOptions.OfficialApiBaseUrl)]
    public void Em_Production_URL_nao_oficial_sem_AllowNonOfficialEndpoints_falha(string authorizationBaseUrl, string apiBaseUrl)
    {
        // Dado Production com uma URL diferente da oficial e AllowNonOfficialEndpoints=false
        var options = Configured(authorizationBaseUrl, apiBaseUrl, allowNonOfficial: false);

        // Quando validadas; então a validação falha (a subida é impedida)
        var result = Validator(Environments.Production).Validate(null, options);
        Assert.True(result.Failed, "URL não oficial em Production deveria falhar a validação.");
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-04")]
    public void Em_Production_com_URLs_oficiais_passa()
    {
        var result = Validator(Environments.Production).Validate(null, Configured(
            MercadoLivreOptions.OfficialAuthorizationBaseUrl, MercadoLivreOptions.OfficialApiBaseUrl, allowNonOfficial: false));
        Assert.True(result.Succeeded, $"URLs oficiais em Production deveriam passar: {result.FailureMessage}");
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-04")]
    public void Em_Production_com_AllowNonOfficialEndpoints_true_URL_nao_oficial_passa()
    {
        var result = Validator(Environments.Production).Validate(null, Configured(NonOfficialAuth, NonOfficialApi, allowNonOfficial: true));
        Assert.True(result.Succeeded, $"AllowNonOfficialEndpoints=true deveria aceitar URL não oficial: {result.FailureMessage}");
    }

    [Theory]
    [Trait("spec", "SPEC-0020:UT-04")]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Fora_de_Production_URL_nao_oficial_passa(string environment)
    {
        var result = Validator(environment).Validate(null, Configured(NonOfficialAuth, NonOfficialApi, allowNonOfficial: false));
        Assert.True(result.Succeeded, $"Fora de Production URL não oficial deveria passar: {result.FailureMessage}");
    }

    [Theory]
    [Trait("spec", "SPEC-0020:UT-04")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void RedirectUri_sem_HTTPS_fora_de_Development_falha(string environment)
    {
        // Dado RedirectUri em HTTP fora de Development
        var options = Configured(MercadoLivreOptions.OfficialAuthorizationBaseUrl, MercadoLivreOptions.OfficialApiBaseUrl, allowNonOfficial: true);
        options.RedirectUri = "http://vitrine.localhost/contas/mercado-livre/retorno";

        // Quando validadas; então falha
        Assert.True(Validator(environment).Validate(null, options).Failed, "RedirectUri sem HTTPS fora de Development deveria falhar.");
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-04")]
    public void RedirectUri_sem_HTTPS_em_Development_passa()
    {
        var options = Configured(MercadoLivreOptions.OfficialAuthorizationBaseUrl, MercadoLivreOptions.OfficialApiBaseUrl, allowNonOfficial: false);
        options.RedirectUri = "http://localhost:5173/contas/mercado-livre/retorno";

        var result = Validator(Environments.Development).Validate(null, options);
        Assert.True(result.Succeeded, $"RedirectUri HTTP em Development deveria passar: {result.FailureMessage}");
    }

    [Theory]
    [Trait("spec", "SPEC-0020:UT-04")]
    [InlineData("Production")]
    [InlineData("Development")]
    public void ML_nao_configurado_nao_impede_a_subida(string environment)
    {
        // Dado nenhuma configuração MercadoLivre:* (padrões: URLs oficiais, sem ClientId/ClientSecret/RedirectUri)
        // Quando validadas; então passa — os endpoints respondem 503 ML_NOT_CONFIGURED (Plano de Rollout)
        var result = Validator(environment).Validate(null, new MercadoLivreOptions());
        Assert.True(result.Succeeded, $"Sem configuração do ML a subida não pode falhar: {result.FailureMessage}");
    }

    private static MercadoLivreOptions Configured(string authorizationBaseUrl, string apiBaseUrl, bool allowNonOfficial) =>
        new()
        {
            ClientId = "1234567890123456",
            ClientSecret = "segredo-de-teste-falso",
            RedirectUri = "https://vitrine.localhost/contas/mercado-livre/retorno",
            AuthorizationBaseUrl = authorizationBaseUrl,
            ApiBaseUrl = apiBaseUrl,
            AllowNonOfficialEndpoints = allowNonOfficial,
        };

    private static MercadoLivreOptionsValidator Validator(string environment) => new(new TestHostEnvironment(environment));

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Vitrine.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
