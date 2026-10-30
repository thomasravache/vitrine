using Vitrine.ArchitectureTests.Support;

namespace Vitrine.ArchitectureTests;

public sealed class SolutionArchitectureLoaderTests : IDisposable
{
    private const string ProjectName = "Vitrine.NaoReferenciado";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitrine-spec-0027-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    [Trait("spec", "SPEC-0027:UT-01")]
    public void Projeto_de_src_cujo_assembly_so_existe_em_src_bin_falha_apontando_o_projeto_nao_referenciado()
    {
        // Dado um repositório com um projeto em src/ cujo assembly existe só em src/<projeto>/bin
        var repositoryRoot = Path.Combine(_root, "repo");
        var projectDirectory = Path.Combine(repositoryRoot, "src", ProjectName);
        var projectBin = Path.Combine(projectDirectory, "bin", "Debug", "net10.0");
        var testsOutput = Path.Combine(_root, "tests-output");
        Directory.CreateDirectory(projectBin);
        Directory.CreateDirectory(testsOutput);
        File.WriteAllText(Path.Combine(repositoryRoot, "Vitrine.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(projectDirectory, ProjectName + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.Copy(typeof(SolutionArchitecture).Assembly.Location, Path.Combine(projectBin, ProjectName + ".dll"));

        // Quando o carregador de assemblies da solução roda com a saída do projeto de testes (sem esse assembly)
        var error = Record.Exception(() =>
            SolutionArchitecture.LoadProjectAssemblies(repositoryRoot, testsOutput, "Debug", "net10.0"));

        // Então falha com mensagem que aponta o projeto não referenciado
        Assert.True(
            error is InvalidOperationException,
            $"Esperava InvalidOperationException; o carregador retornou {(error is null ? "sem erro (usou src/**/bin)" : error.GetType().Name)}.");
        Assert.Contains("não é referenciado", error!.Message, StringComparison.Ordinal);
        Assert.Contains(ProjectName, error.Message, StringComparison.Ordinal);
    }
}
