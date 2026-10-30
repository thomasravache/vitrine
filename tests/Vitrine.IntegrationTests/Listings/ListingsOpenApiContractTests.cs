using System.Text.Json;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:CT-02 — o documento OpenAPI gerado (<c>web/src/api/openapi.json</c>, fonte do cliente da SPA — ADR-0013;
/// o CI barra divergência com a Api) descreve <c>GET /listings</c>, <c>GET /listings/summary</c> e
/// <c>POST /listings/sync</c> com os campos, tipos e códigos de resposta da seção 6. A SPEC-0025 consome esse contrato.
/// </summary>
public sealed class ListingsOpenApiContractTests
{
    private static readonly Lazy<JsonElement> Document = new(LoadDocument);

    [Fact]
    [Trait("spec", "SPEC-0022:CT-02")]
    public void GET_listings_tem_os_parametros_a_pagina_com_os_campos_do_item_e_os_codigos_do_contrato()
    {
        var operation = Operation("/api/v1/listings", "get");

        // Parâmetros de query: status, accountId (uuid), q, limit (inteiro) e cursor
        var parameters = operation.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, StringComparer.Ordinal);
        foreach (var name in new[] { "status", "accountId", "q", "limit", "cursor" })
        {
            Assert.True(parameters.TryGetValue(name, out var parameter), $"GET /api/v1/listings sem o parâmetro {name}.");
            Assert.Equal("query", parameter.GetProperty("in").GetString());
        }

        AssertType(parameters["accountId"].GetProperty("schema"), "string", "uuid", "parâmetro accountId");
        AssertType(parameters["limit"].GetProperty("schema"), "integer", null, "parâmetro limit");
        AssertType(parameters["status"].GetProperty("schema"), "string", null, "parâmetro status");
        AssertType(parameters["q"].GetProperty("schema"), "string", null, "parâmetro q");
        AssertType(parameters["cursor"].GetProperty("schema"), "string", null, "parâmetro cursor");

        // 200: { items: [item], nextCursor: string|null }
        var page = Resolve(JsonResponse(operation, "200"));
        AssertType(Property(page, "nextCursor"), "string", null, "nextCursor", nullable: true);
        var items = Resolve(Property(page, "items"));
        AssertType(items, "array", null, "items");
        var item = Resolve(items.GetProperty("items"));
        AssertType(Property(item, "itemId"), "string", null, "itemId");
        AssertType(Property(item, "accountId"), "string", "uuid", "accountId");
        AssertType(Property(item, "title"), "string", null, "title");
        AssertType(Property(item, "price"), "number", null, "price");
        AssertType(Property(item, "currencyId"), "string", null, "currencyId");
        AssertType(Property(item, "availableQuantity"), "integer", null, "availableQuantity");
        AssertType(Property(item, "soldQuantity"), "integer", null, "soldQuantity");
        AssertType(Property(item, "status"), "string", null, "status");
        AssertType(Property(item, "permalink"), "string", null, "permalink", nullable: true);
        AssertType(Property(item, "thumbnailUrl"), "string", null, "thumbnailUrl", nullable: true);
        AssertType(Property(item, "lastUpdated"), "string", "date-time", "lastUpdated");

        AssertResponses(operation, "200", "400", "401", "403");
        AssertProblem(operation, "400");
    }

    [Fact]
    [Trait("spec", "SPEC-0022:CT-02")]
    public void GET_listings_summary_tem_totais_por_status_e_estado_por_conta()
    {
        var operation = Operation("/api/v1/listings/summary", "get");

        // 200: { total, byStatus: { active, paused, closed, other }, accounts: [...] }
        var summary = Resolve(JsonResponse(operation, "200"));
        AssertType(Property(summary, "total"), "integer", null, "total");
        var byStatus = Resolve(Property(summary, "byStatus"));
        foreach (var status in new[] { "active", "paused", "closed", "other" })
        {
            AssertType(Property(byStatus, status), "integer", null, $"byStatus.{status}");
        }

        var accounts = Resolve(Property(summary, "accounts"));
        AssertType(accounts, "array", null, "accounts");
        var account = Resolve(accounts.GetProperty("items"));
        AssertType(Property(account, "accountId"), "string", "uuid", "accounts.accountId");
        AssertType(Property(account, "itemCount"), "integer", null, "accounts.itemCount");
        AssertType(Property(account, "syncStatus"), "string", null, "accounts.syncStatus");
        AssertType(Property(account, "lastSyncedAt"), "string", "date-time", "accounts.lastSyncedAt", nullable: true);
        AssertType(Property(account, "lastError"), "string", null, "accounts.lastError", nullable: true);

        AssertResponses(operation, "200", "401", "403");
    }

    [Fact]
    [Trait("spec", "SPEC-0022:CT-02")]
    public void POST_listings_sync_aceita_accountId_opcional_e_documenta_202_404_409_429()
    {
        var operation = Operation("/api/v1/listings/sync", "post");

        // body: { accountId?: uuid }
        Assert.True(operation.TryGetProperty("requestBody", out var body), "POST /api/v1/listings/sync sem requestBody.");
        var request = Resolve(body.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        AssertType(Property(request, "accountId"), "string", "uuid", "accountId", nullable: true);
        if (request.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain("accountId", required.EnumerateArray().Select(field => field.GetString()));
        }

        // 202: { queued: [uuid] }
        var accepted = Resolve(JsonResponse(operation, "202"));
        var queued = Resolve(Property(accepted, "queued"));
        AssertType(queued, "array", null, "queued");
        AssertType(Resolve(queued.GetProperty("items")), "string", "uuid", "queued[]");

        AssertResponses(operation, "202", "400", "401", "403", "404", "409", "429");
        foreach (var status in new[] { "400", "404", "409", "429" })
        {
            AssertProblem(operation, status);
        }
    }

    private static JsonElement LoadDocument()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Vitrine.slnx")))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "web", "src", "api", "openapi.json")));
                return document.RootElement.Clone();
            }
        }

        throw new InvalidOperationException($"Vitrine.slnx não encontrado acima de '{AppContext.BaseDirectory}'.");
    }

    private static JsonElement Operation(string path, string method)
    {
        Assert.True(
            Document.Value.GetProperty("paths").TryGetProperty(path, out var item),
            $"web/src/api/openapi.json não tem {path} (rode `npm run api:generate` em web/ depois de mapear o endpoint).");
        Assert.True(item.TryGetProperty(method, out var operation), $"{path} sem a operação {method.ToUpperInvariant()}.");
        return operation;
    }

    private static JsonElement JsonResponse(JsonElement operation, string status)
    {
        Assert.True(operation.GetProperty("responses").TryGetProperty(status, out var response), $"Operação sem a resposta {status}.");
        var json = default(JsonElement);
        Assert.True(
            response.TryGetProperty("content", out var content) && content.TryGetProperty("application/json", out json),
            $"Resposta {status} sem conteúdo application/json.");
        return json.GetProperty("schema");
    }

    private static void AssertResponses(JsonElement operation, params string[] statuses)
    {
        var responses = operation.GetProperty("responses");
        foreach (var status in statuses)
        {
            Assert.True(responses.TryGetProperty(status, out _), $"Operação {operation.GetProperty("operationId")} sem a resposta {status}.");
        }
    }

    private static void AssertProblem(JsonElement operation, string status)
    {
        var response = operation.GetProperty("responses").GetProperty(status);
        Assert.True(
            response.TryGetProperty("content", out var content) && content.TryGetProperty("application/problem+json", out _),
            $"Resposta {status} de {operation.GetProperty("operationId")} sem application/problem+json.");
    }

    private static JsonElement Resolve(JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!["#/components/schemas/".Length..];
            schema = Document.Value.GetProperty("components").GetProperty("schemas").GetProperty(name);
        }

        return schema;
    }

    private static JsonElement Property(JsonElement schema, string name)
    {
        var property = default(JsonElement);
        Assert.True(
            schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(name, out property),
            $"Schema sem a propriedade {name}: {schema}");
        return property;
    }

    private static void AssertType(JsonElement schema, string type, string? format, string what, bool nullable = false)
    {
        schema = Resolve(schema);
        var types = schema.TryGetProperty("type", out var declared)
            ? declared.ValueKind == JsonValueKind.Array ? declared.EnumerateArray().Select(value => value.GetString()!).ToList() : [declared.GetString()!]
            : [];
        Assert.True(types.Contains(type), $"{what}: tipo esperado {type}, documentado [{string.Join(", ", types)}] em {schema}");
        if (format is not null)
        {
            Assert.True(
                schema.TryGetProperty("format", out var actual) && actual.GetString() == format,
                $"{what}: formato esperado {format} em {schema}");
        }

        if (nullable)
        {
            Assert.True(types.Contains("null"), $"{what}: deveria aceitar null (tipo [{string.Join(", ", types)}]).");
        }
    }
}
