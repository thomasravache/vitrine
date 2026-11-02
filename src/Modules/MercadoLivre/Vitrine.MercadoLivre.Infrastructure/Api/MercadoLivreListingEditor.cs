using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Vitrine.MercadoLivre.Infrastructure.Diagnostics;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <see cref="IMercadoLivreListingEditor"/> pelo cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0033,
/// ADR-0010): relê o anúncio e a categoria, aplica <see cref="ListingChangeRules"/> (campo violado não é enviado), envia
/// título e atributos num <c>PUT /items/{id}</c> e depois a descrição (<c>PUT ?api_version=2</c> quando já existe,
/// <c>POST</c> quando não), e devolve o resultado por campo. Escritas não são repetidas pelo pipeline: só o 409 (uma vez,
/// depois de 2 s pelo <see cref="TimeProvider"/> do contêiner), o reenvio sem o título recusado e o reenvio depois de um
/// 401 com renovação forçada (no handler do token). Indisponibilidade numa escrita interrompe as seguintes.
/// </summary>
internal sealed partial class MercadoLivreListingEditor(
    IMercadoLivreListingDetails details,
    MercadoLivreApiCaller api,
    MercadoLivreMetrics metrics,
    TimeProvider clock,
    ILogger<MercadoLivreListingEditor> logger) : IMercadoLivreListingEditor
{
    /// <summary>Espera antes da única nova tentativa depois de um 409 (optimistic locking).</summary>
    public static readonly TimeSpan ConflictRetryDelay = TimeSpan.FromSeconds(2);

    private const string TitleField = "title";
    private const string DescriptionField = "description";
    private const string AttributesField = "attributes";

    public async Task<ListingChangeResult> ApplyAsync(
        Guid accountId, string itemId, ListingChanges changes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(changes);
        var duplicated = (changes.Attributes ?? []).GroupBy(change => change.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicated is not null)
        {
            // Emenda v1.1: o mesmo atributo duas vezes é erro do chamador (o resultado é por id), antes de qualquer leitura.
            throw new ArgumentException($"Atributo {duplicated.Key} repetido nas mudanças.", nameof(changes));
        }

        // 1. Releitura: a verdade é o ML, não o que o chamador mandou. Falha aqui lança e nada é escrito.
        var item = await details.GetItemDetailsAsync(accountId, itemId, cancellationToken)
            ?? throw new MercadoLivreItemNotFoundException(accountId, itemId);
        var category = await details.GetCategoryAsync(accountId, item.CategoryId, cancellationToken)
            ?? throw new MercadoLivreUnavailableException(accountId, "categoria do anúncio não encontrada");

        // 2. Regras locais: campo violado fica NotAttempted(código) e não vai ao ML.
        var violations = ListingChangeRules.Violations(item, category, changes);
        var requested = changes.Attributes ?? [];
        if (violations.TryGetValue("item", out var itemCode))
        {
            FieldResult notActive = new FieldResult.NotAttempted(itemCode);
            return Finish(
                accountId,
                itemId,
                changes.Title is null ? null : notActive,
                changes.Description is null ? null : notActive,
                requested.ToDictionary(change => change.Id, _ => notActive, StringComparer.Ordinal));
        }

        FieldResult? title = null;
        string? pendingTitle = null;
        if (changes.Title is not null)
        {
            if (violations.TryGetValue("title", out var code))
            {
                title = new FieldResult.NotAttempted(code);
            }
            else
            {
                pendingTitle = changes.Title;
            }
        }

        var attributes = new Dictionary<string, FieldResult>(StringComparer.Ordinal);
        var pendingAttributes = new List<AttributeChange>();
        foreach (var change in requested)
        {
            if (violations.TryGetValue($"attributes.{change.Id}", out var code))
            {
                attributes[change.Id] = new FieldResult.NotAttempted(code);
                continue;
            }

            var spec = category.Attributes.First(attribute => string.Equals(attribute.Id, change.Id, StringComparison.Ordinal));
            pendingAttributes.Add(ListingChangeRules.Normalize(spec, change));
        }

        FieldResult? description = null;
        string? pendingDescription = null;
        if (changes.Description is not null)
        {
            if (violations.TryGetValue("description", out var code))
            {
                description = new FieldResult.NotAttempted(code);
            }
            else
            {
                pendingDescription = changes.Description;
            }
        }

        // 3. Título e atributos num único PUT /items/{id}; 4. descrição. Uma exceção depois de escritas concluídas (2º 401,
        // cancelamento) registra os campos já decididos antes de propagar (emenda v1.1).
        try
        {
            var interrupted = false;
            if (pendingTitle is not null || pendingAttributes.Count > 0)
            {
                var path = MercadoLivreListingDetailsGateway.ItemPath(itemId);
                var outcome = await WriteAsync(accountId, itemId, HttpMethod.Put, path, ItemBody(pendingTitle, pendingAttributes), cancellationToken);
                if (outcome is FieldResult.Rejected { Code: MercadoLivreErrorMapper.FieldNotUpdatable } && pendingTitle is not null && pendingAttributes.Count > 0)
                {
                    // Título recusado pelo ML: um reenvio só com os atributos.
                    title = outcome;
                    outcome = await WriteAsync(accountId, itemId, HttpMethod.Put, path, ItemBody(null, pendingAttributes), cancellationToken);
                }
                else if (pendingTitle is not null)
                {
                    title = outcome;
                }

                foreach (var change in pendingAttributes)
                {
                    attributes[change.Id] = outcome;
                }

                interrupted = IsUnavailable(outcome);
            }

            // 4. Descrição em texto puro, depois do item; não é tentada se o ML ficou indisponível.
            if (pendingDescription is not null)
            {
                if (interrupted)
                {
                    description = new FieldResult.NotAttempted(MercadoLivreErrorMapper.Unavailable);
                }
                else
                {
                    var path = $"{MercadoLivreListingDetailsGateway.ItemPath(itemId)}/description";
                    var body = new JsonObject { ["plain_text"] = pendingDescription };
                    description = item.Description is null
                        ? await WriteAsync(accountId, itemId, HttpMethod.Post, path, body, cancellationToken)
                        : await WriteAsync(accountId, itemId, HttpMethod.Put, $"{path}?api_version=2", body, cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Interrupted(logger, accountId, itemId, exception.GetType().Name);
            Finish(accountId, itemId, title, description, attributes);
            throw;
        }

        return Finish(accountId, itemId, title, description, attributes);
    }

    /// <summary>
    /// <c>{ "title"?: string, "attributes"?: [{ "id", "value_id" | "value_name" }] }</c>: campos ausentes não são enviados.
    /// </summary>
    private static JsonObject ItemBody(string? title, List<AttributeChange> attributes)
    {
        var body = new JsonObject();
        if (title is not null)
        {
            body["title"] = title;
        }

        if (attributes.Count > 0)
        {
            var array = new JsonArray();
            foreach (var attribute in attributes)
            {
                var entry = new JsonObject { ["id"] = attribute.Id };
                if (attribute.ValueId is not null)
                {
                    entry["value_id"] = attribute.ValueId;
                }
                else
                {
                    entry["value_name"] = attribute.ValueName;
                }

                array.Add(entry);
            }

            body["attributes"] = array;
        }

        return body;
    }

    /// <summary>Uma escrita, com uma única nova tentativa depois de 2 s em caso de 409.</summary>
    private async Task<FieldResult> WriteAsync(
        Guid accountId, string itemId, HttpMethod method, string relativeUrl, JsonObject body, CancellationToken cancellationToken)
    {
        var endpoint = relativeUrl.Contains("/description", StringComparison.Ordinal)
            ? MercadoLivreListingDetailsGateway.DescriptionEndpoint
            : MercadoLivreListingDetailsGateway.ItemEndpoint;
        for (var attempt = 1; ; attempt++)
        {
            var response = await api.WriteAsync(accountId, method, endpoint, relativeUrl, body, cancellationToken);
            switch (response)
            {
                case WriteResponse.Unreachable unreachable:
                    return MercadoLivreErrorMapper.UnavailableResult(unreachable.Reason);
                case WriteResponse.Answered { StatusCode: >= 200 and < 300 }:
                    return new FieldResult.Applied();
                case WriteResponse.Answered { StatusCode: 409 } when attempt == 1:
                    Log.ConflictRetry(logger, accountId, itemId, endpoint);
                    await Task.Delay(ConflictRetryDelay, clock, cancellationToken);
                    continue;
                case WriteResponse.Answered answered:
                    return MercadoLivreErrorMapper.Map(answered.StatusCode, answered.Body);
                default:
                    throw new InvalidOperationException($"Resposta de escrita inesperada: {response.GetType().Name}.");
            }
        }
    }

    private static bool IsUnavailable(FieldResult result) =>
        result is FieldResult.Rejected { Code: MercadoLivreErrorMapper.Unavailable };

    /// <summary>Resultado final com métrica (uma medição por campo) e logs (sem conteúdo dos campos).</summary>
    private ListingChangeResult Finish(
        Guid accountId, string itemId, FieldResult? title, FieldResult? description, Dictionary<string, FieldResult> attributes)
    {
        Record(accountId, itemId, TitleField, title);
        Record(accountId, itemId, DescriptionField, description);

        // Atributos contam como um campo: uma medição por resultado distinto (normalmente um só).
        foreach (var result in attributes.Values.Distinct())
        {
            Record(accountId, itemId, AttributesField, result);
        }

        return new ListingChangeResult(title, description, attributes);
    }

    private void Record(Guid accountId, string itemId, string field, FieldResult? result)
    {
        switch (result)
        {
            case FieldResult.Applied:
                metrics.ListingEdited(field, "applied", "none");
                Log.FieldApplied(logger, accountId, itemId, field);
                break;
            case FieldResult.Rejected rejected:
                metrics.ListingEdited(field, "rejected", rejected.Code);
                Log.FieldRejected(logger, accountId, itemId, field, rejected.Code);
                break;
            case FieldResult.NotAttempted notAttempted:
                metrics.ListingEdited(field, "not_attempted", notAttempted.Reason);
                Log.FieldNotAttempted(logger, accountId, itemId, field, notAttempted.Reason);
                break;
        }
    }

    /// <summary>Mensagens de log (ids, campo e código; nunca conteúdo dos campos nem tokens).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "Anúncio {ItemId} da conta do Mercado Livre {AccountId}: {Field} aplicado.")]
        public static partial void FieldApplied(ILogger logger, Guid accountId, string itemId, string field);

        [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "Anúncio {ItemId} da conta do Mercado Livre {AccountId}: {Field} recusado ({Code}).")]
        public static partial void FieldRejected(ILogger logger, Guid accountId, string itemId, string field, string code);

        [LoggerMessage(EventId = 43, Level = LogLevel.Information, Message = "Anúncio {ItemId} da conta do Mercado Livre {AccountId}: {Field} não enviado ({Code}).")]
        public static partial void FieldNotAttempted(ILogger logger, Guid accountId, string itemId, string field, string code);

        [LoggerMessage(EventId = 44, Level = LogLevel.Information, Message = "Anúncio {ItemId} da conta do Mercado Livre {AccountId}: 409 em {Endpoint}; nova tentativa em 2 s.")]
        public static partial void ConflictRetry(ILogger logger, Guid accountId, string itemId, string endpoint);

        [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "Anúncio {ItemId} da conta do Mercado Livre {AccountId}: edição interrompida por {Failure}; campos já decididos registrados.")]
        public static partial void Interrupted(ILogger logger, Guid accountId, string itemId, string failure);
    }
}
