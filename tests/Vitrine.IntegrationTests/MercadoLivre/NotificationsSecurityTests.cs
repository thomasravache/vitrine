using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Security.Support;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.MercadoLivre;

/// <summary>
/// Segurança do webhook (SPEC-0023): rate limit por IP do cliente (<c>RateLimiting:MercadoLivreNotificationsPerIp</c>,
/// 600/min no contrato; 5/min aqui), e o endpoint anônimo (sem cookie nem antiforgery), na lista
/// <c>AnonymousEndpoints</c> (emenda v5 da SPEC-0008) e fora do documento OpenAPI.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationsSecurityTests(PostgresFixture postgres)
{
    private const int TestLimit = 5;

    [Fact]
    [Trait("spec", "SPEC-0023:IT-06")]
    public async Task O_6o_POST_do_mesmo_IP_no_minuto_recebe_429_com_Retry_After_e_outro_IP_nao_e_afetado()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        // Dado o limite configurado em 5/min
        await using var factory = MercadoLivreHost.Create(connectionString)
            .WithSetting(MercadoLivreNotifications.PerIpLimitSetting, TestLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var client = MercadoLivreNotifications.Client(factory, TestAddresses.Client(80));

        // Quando 5 POSTs do mesmo IP chegam no minuto, todos são atendidos
        for (var request = 0; request < TestLimit; request++)
        {
            using var accepted = await MercadoLivreNotifications.PostAsync(
                client, MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId()), cancellationToken);
            await MercadoLivreNotifications.AssertEmptyOkAsync(accepted, cancellationToken);
        }

        // Então o 6º recebe 429 com Retry-After, sem refletir o corpo e sem gravar
        var marker = "marcador-" + Guid.NewGuid().ToString("N");
        var sixth = MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId());
        sixth["_id"] = marker;
        using (var limited = await MercadoLivreNotifications.PostAsync(client, sixth, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            var retryAfter = limited.Headers.RetryAfter?.Delta;
            Assert.True(retryAfter is { } delta && delta > TimeSpan.Zero, "429 sem Retry-After (segundos) válido.");
            var body = await limited.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
            Assert.Equal("TOO_MANY_REQUESTS", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        }

        Assert.Empty(await MercadoLivreNotifications.ReadAsync(connectionString, marker, cancellationToken));

        // E outro IP não é afetado
        using var otherClient = MercadoLivreNotifications.Client(factory, TestAddresses.Client(81));
        using var other = await MercadoLivreNotifications.PostAsync(
            otherClient, MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId()), cancellationToken);
        await MercadoLivreNotifications.AssertEmptyOkAsync(other, cancellationToken);
    }

    [Fact]
    [Trait("spec", "SPEC-0023:IT-07")]
    public async Task Webhook_e_anonimo_sem_antiforgery_esta_na_lista_AnonymousEndpoints_e_fora_do_OpenAPI()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await using var factory = MercadoLivreHost.Create(connectionString);

        // Dado a Api; quando o ML faz POST sem cookie e sem antiforgery, então é aceito (200 vazio)
        using (var client = MercadoLivreNotifications.Client(factory, TestAddresses.Client(82)))
        {
            using var response = await MercadoLivreNotifications.PostAsync(
                client, MercadoLivreNotifications.Items(MlSeller.New().Id, MercadoLivreNotifications.NewItemId()), cancellationToken);
            await MercadoLivreNotifications.AssertEmptyOkAsync(response, cancellationToken);
            Assert.False(response.Headers.Contains("Set-Cookie"), "O webhook não deveria emitir cookie.");
        }

        // E o endpoint existe só com POST, está na lista AnonymousEndpoints e a regra de autorização por padrão passa
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var webhook = Assert.Single(
            endpoints.OfType<RouteEndpoint>(),
            endpoint => string.Equals(endpoint.RoutePattern.RawText, MercadoLivreNotifications.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["POST"], AnonymousEndpoints.Methods(webhook));
        Assert.Contains(new AnonymousEndpoints.Entry("POST", MercadoLivreNotifications.Path), AnonymousEndpoints.Allowed);
        Assert.True(AnonymousEndpoints.IsAllowed(webhook));
        var violations = await AnonymousEndpoints.ViolationsAsync(endpoints, factory.Services.GetRequiredService<IAuthorizationPolicyProvider>());
        Assert.True(violations.Count == 0, "Endpoints anônimos fora da lista AnonymousEndpoints: " + string.Join("; ", violations));

        // E o documento OpenAPI (ApiExplorer, de onde ele é gerado) não contém o caminho — e contém os outros do módulo
        var paths = factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(description => "/" + description.RelativePath)
            .ToList();
        Assert.Contains("/api/v1/mercadolivre/accounts", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith(MercadoLivreNotifications.Path, StringComparison.OrdinalIgnoreCase));

        // E nem o documento gerado e versionado (web/src/api/openapi.json, ADR-0013)
        var document = await File.ReadAllTextAsync(GeneratedOpenApiDocument(), cancellationToken);
        Assert.DoesNotContain("/api/v1/mercadolivre/notifications", document, StringComparison.OrdinalIgnoreCase);
    }

    private static string GeneratedOpenApiDocument()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Vitrine.slnx")))
            {
                return Path.Combine(dir.FullName, "web", "src", "api", "openapi.json");
            }
        }

        throw new InvalidOperationException($"Vitrine.slnx não encontrado acima de '{AppContext.BaseDirectory}'.");
    }
}
