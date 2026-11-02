using System.Globalization;
using System.Text.Json.Nodes;
using WireMock;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>
/// Resposta de uma escrita no ML simulado (<c>PUT</c>/<c>POST</c>). Com <see cref="RequiredToken"/>, um Bearer diferente
/// recebe 401 sem consumir a resposta (token rejeitado: nada foi aplicado).
/// </summary>
internal sealed record MlReply(int Status, string? Body = null, string? RequiredToken = null)
{
    /// <summary>Erro a partir de uma fixture <c>error-*.json</c> (<c>{ status, response }</c>).</summary>
    public static MlReply FromErrorFixture(string name)
    {
        var fixture = ListingFixtures.Load(name);
        return new MlReply(fixture["status"]!.GetValue<int>(), fixture["response"]!.ToJsonString());
    }

    /// <summary>Erro no formato do ML (<c>{ message, error, status, cause }</c>).</summary>
    public static MlReply Error(int status, string error = "internal_error") =>
        new(status, new JsonObject
        {
            ["message"] = $"erro simulado: {error}",
            ["error"] = error,
            ["status"] = status,
            ["cause"] = new JsonArray(),
        }.ToJsonString());
}

/// <summary>
/// Endpoints de anúncio e categoria do ML simulado no WireMock para a SPEC-0033: <c>GET /items/{id}</c>,
/// <c>GET /items/{id}/description</c>, <c>GET /categories/{id}</c>, <c>GET /categories/{id}/attributes</c> e as escritas
/// <c>PUT /items/{id}</c>, <c>PUT /items/{id}/description</c> e <c>POST /items/{id}/description</c>, com respostas em
/// sequência (a última se repete). Os corpos de leitura vêm das fixtures em <c>tests/Fixtures/MercadoLivre</c>.
/// </summary>
internal sealed class MercadoLivreListingFake(WireMockServer server)
{
    /// <summary>Requisições recebidas pelo ML simulado, em ordem de chegada.</summary>
    public IReadOnlyList<ILogEntry> Calls =>
        [.. server.LogEntries.OrderBy(entry => entry.RequestMessage?.DateTime ?? DateTime.MinValue)];

    public static string ItemPath(string itemId) => $"/items/{itemId}";

    public static string DescriptionPath(string itemId) => $"/items/{itemId}/description";

    public static string CategoryPath(string categoryId) => $"/categories/{categoryId}";

    public static string CategoryAttributesPath(string categoryId) => $"/categories/{categoryId}/attributes";

    /// <summary>Requisições recebidas com <paramref name="method"/> em <paramref name="path"/> (sem a query).</summary>
    public IReadOnlyList<ILogEntry> CallsTo(string method, string path) =>
        [.. Calls.Where(entry => entry.RequestMessage?.Path == path
            && string.Equals(entry.RequestMessage?.Method, method, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Escritas (PUT/POST) recebidas no anúncio ou na descrição dele.</summary>
    public IReadOnlyList<ILogEntry> Writes(string itemId) =>
        [.. Calls.Where(entry => (entry.RequestMessage?.Path == ItemPath(itemId) || entry.RequestMessage?.Path == DescriptionPath(itemId))
            && entry.RequestMessage?.Method is { } method
            && (method.Equals("PUT", StringComparison.OrdinalIgnoreCase) || method.Equals("POST", StringComparison.OrdinalIgnoreCase)))];

    /// <summary>Corpo JSON de uma requisição registrada.</summary>
    public static JsonNode? BodyOf(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var body = entry.RequestMessage?.Body;
        return string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body);
    }

    /// <summary><c>GET /items/{id}</c> (qualquer query) responde 200 com <paramref name="item"/> (o id vem do corpo).</summary>
    public void ItemResponds(JsonNode item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var itemId = item["id"]!.GetValue<string>();
        Get(ItemPath(itemId), 200, item.ToJsonString());
    }

    /// <summary>Toda chamada de <c>GET /items/{id}</c> responde <paramref name="status"/>.</summary>
    public void ItemFails(string itemId, int status) =>
        Get(ItemPath(itemId), status, MlReply.Error(status, status == 404 ? "not_found" : "internal_error").Body!);

    /// <summary><c>GET /items/{id}/description</c> responde 200 com <paramref name="description"/>.</summary>
    public void DescriptionResponds(string itemId, JsonNode description)
    {
        ArgumentNullException.ThrowIfNull(description);
        Get(DescriptionPath(itemId), 200, description.ToJsonString());
    }

    /// <summary><c>GET /items/{id}/description</c> responde 404 (anúncio sem descrição).</summary>
    public void DescriptionMissing(string itemId) => Get(DescriptionPath(itemId), 404, MlReply.Error(404, "not_found").Body!);

    /// <summary><c>GET /categories/{id}</c> e <c>GET /categories/{id}/attributes</c> respondem 200.</summary>
    public void CategoryResponds(JsonNode category, JsonNode attributes)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(attributes);
        var categoryId = category["id"]!.GetValue<string>();
        Get(CategoryPath(categoryId), 200, category.ToJsonString());
        Get(CategoryAttributesPath(categoryId), 200, attributes.ToJsonString());
    }

    /// <summary><c>GET /categories/{id}</c> e <c>/attributes</c> respondem 404 (categoria inexistente).</summary>
    public void CategoryMissing(string categoryId)
    {
        Get(CategoryPath(categoryId), 404, MlReply.Error(404, "not_found").Body!);
        Get(CategoryAttributesPath(categoryId), 404, MlReply.Error(404, "not_found").Body!);
    }

    /// <summary><c>PUT /items/{id}</c> responde <paramref name="replies"/> em sequência (padrão: 200).</summary>
    public void ItemUpdateResponds(string itemId, params MlReply[] replies) =>
        Write("PUT", ItemPath(itemId), replies.Length > 0 ? replies : [new MlReply(200, new JsonObject { ["id"] = itemId }.ToJsonString())]);

    /// <summary><c>PUT /items/{id}/description</c> responde <paramref name="replies"/> em sequência (padrão: 200).</summary>
    public void DescriptionUpdateResponds(string itemId, params MlReply[] replies) =>
        Write("PUT", DescriptionPath(itemId), replies.Length > 0 ? replies : [new MlReply(200, """{"plain_text":""}""")]);

    /// <summary><c>POST /items/{id}/description</c> responde <paramref name="replies"/> em sequência (padrão: 201).</summary>
    public void DescriptionCreateResponds(string itemId, params MlReply[] replies) =>
        Write("POST", DescriptionPath(itemId), replies.Length > 0 ? replies : [new MlReply(201, """{"plain_text":""}""")]);

    /// <summary>Todas as escritas do anúncio respondem com sucesso (PUT 200, POST 201).</summary>
    public void WritesSucceed(string itemId)
    {
        ItemUpdateResponds(itemId);
        DescriptionUpdateResponds(itemId);
        DescriptionCreateResponds(itemId);
    }

    /// <summary>Valor do cabeçalho <c>Authorization</c> de uma requisição registrada.</summary>
    public static string? AuthorizationOf(ILogEntry entry) => MercadoLivreFake.AuthorizationOf(entry);

    private void Get(string path, int status, string body) =>
        server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithCallback(_ => Message(status, body)));

    private void Write(string method, string path, MlReply[] replies)
    {
        var next = 0;
        server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(Response.Create().WithCallback(request =>
        {
            var index = Math.Min(Volatile.Read(ref next), replies.Length - 1);
            var reply = replies[index];
            if (reply.RequiredToken is { } token
                && !(request.Headers is { } headers
                    && headers.TryGetValue("Authorization", out var authorization)
                    && authorization.ToString() == $"Bearer {token}"))
            {
                return Message(401, MlReply.Error(401, "unauthorized").Body!);
            }

            Interlocked.Increment(ref next);
            return Message(reply.Status, reply.Body ?? MlReply.Error(reply.Status).Body!);
        }));
    }

    private static ResponseMessage Message(int status, string body) => new()
    {
        StatusCode = status,
        Headers = new Dictionary<string, WireMockList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new("application/json"),
        },
        BodyData = new BodyData
        {
            DetectedBodyType = BodyType.String,
            BodyAsString = body,
        },
    };

    /// <summary>IDs <c>MLB</c> sintéticos e únicos por execução, fora da faixa de <see cref="MlItem.NewIds"/>.</summary>
    public static string NewItemId() =>
        string.Create(CultureInfo.InvariantCulture, $"MLB{Interlocked.Increment(ref _nextItem)}");

    private static long _nextItem = 5_200_000_000;
}
