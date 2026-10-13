using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Vitrine.Tooling.Tests;

public sealed class AppHostModelTests
{
    [Fact]
    [Trait("spec", "SPEC-0002:UT-01")]
    public async Task Modelo_do_AppHost_tem_postgres_api_e_web_com_api_referenciando_postgres()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Dado o AppHost; quando o modelo é construído (sem iniciar)
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Vitrine_AppHost>(cancellationToken);

        var resources = builder.Resources.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var existing = string.Join(", ", resources.Keys.Order());

        // Então existem os recursos postgres, api e web
        Assert.True(resources.ContainsKey("postgres"), $"Recurso 'postgres' ausente no AppHost. Recursos: [{existing}]");
        Assert.True(resources.ContainsKey("api"), $"Recurso 'api' ausente no AppHost. Recursos: [{existing}]");
        Assert.True(resources.ContainsKey("web"), $"Recurso 'web' ausente no AppHost. Recursos: [{existing}]");

        // E api referencia postgres (diretamente ou a um banco filho de postgres)
        var postgres = resources["postgres"];
        var api = resources["api"];
        var references = api.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Where(a => a.Type == "Reference")
            .Select(a => a.Resource)
            .ToList();

        Assert.True(
            references.Any(r => IsOrDescendsFrom(r, postgres)),
            $"'api' não referencia 'postgres'. Referências de 'api': [{string.Join(", ", references.Select(r => r.Name))}]");
    }

    private static bool IsOrDescendsFrom(IResource resource, IResource ancestor)
    {
        IResource? current = resource;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = (current as IResourceWithParent)?.Parent;
        }

        return false;
    }
}
