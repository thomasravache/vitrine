using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Vitrine.MercadoLivre.Infrastructure;

namespace Vitrine.UnitTests.MercadoLivre;

/// <summary><c>MercadoLivre:BulkBatchSize</c> só vale de 1 a 100; o padrão é 20 (SPEC-0021, emenda v1.1).</summary>
public sealed class BulkBatchSizeValidationTests
{
    [Theory]
    [Trait("spec", "SPEC-0021:UT-03")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void BulkBatchSize_fora_de_1_a_100_reprova_a_validacao(int bulkBatchSize)
    {
        // Dado MercadoLivre:BulkBatchSize fora de 1..100 (o resto da configuração válido)
        var options = new MercadoLivreOptions { BulkBatchSize = bulkBatchSize };

        // Quando as opções são validadas; então a validação reprova, citando a chave
        var result = Validator().Validate(null, options);
        Assert.True(result.Failed, $"BulkBatchSize = {bulkBatchSize} deveria reprovar a validação.");
        Assert.Contains("BulkBatchSize", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("spec", "SPEC-0021:UT-03")]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(100)]
    public void BulkBatchSize_de_1_a_100_passa(int bulkBatchSize)
    {
        var result = Validator().Validate(null, new MercadoLivreOptions { BulkBatchSize = bulkBatchSize });
        Assert.True(result.Succeeded, $"BulkBatchSize = {bulkBatchSize} deveria passar: {result.FailureMessage}");
    }

    [Fact]
    [Trait("spec", "SPEC-0021:UT-03")]
    public void BulkBatchSize_padrao_e_20()
    {
        Assert.Equal(20, new MercadoLivreOptions().BulkBatchSize);
    }

    private static MercadoLivreOptionsValidator Validator() => new(new TestHostEnvironment(Environments.Production));

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Vitrine.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
