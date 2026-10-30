using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Vitrine.MercadoLivre.Infrastructure.Diagnostics;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// <see cref="IMercadoLivreItems"/> pelo cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> (SPEC-0021): o token
/// é anexado pelo <see cref="MercadoLivreAuthenticationHandler"/> e a resiliência é a do pipeline do cliente. Falhas do
/// ML viram <see cref="MercadoLivreUnavailableException"/> sem mudar a conta; um 401 que persiste depois da renovação
/// forçada leva a conta a <c>reauthorization_required</c>. Cada chamada gera um span (<c>ml.endpoint</c>,
/// <c>ml.account_id</c>).
/// </summary>
/// <remarks>
/// Não pode ser chamado dentro de uma transação que já trave linhas de <c>mercadolivre.accounts</c>: a renovação do token
/// trava a linha da conta em outra conexão e esperaria por esse lock até desistir com
/// <see cref="MercadoLivreUnavailableException"/>.
/// </remarks>
internal sealed class MercadoLivreItemsGateway(
    MercadoLivreModuleDbContext db,
    IHttpClientFactory httpClients,
    MercadoLivreAccessTokenProvider tokens,
    IOptions<MercadoLivreOptions> options) : IMercadoLivreItems
{
    /// <summary>Campos lidos do <c>/items/bulk</c> (contrato da SPEC-0021, seção 6).</summary>
    public const string BulkAttributes =
        "body.id,body.seller_id,body.title,body.price,body.currency_id,body.available_quantity,body.sold_quantity,"
        + "body.status,body.permalink,body.thumbnail,body.secure_thumbnail,body.listing_type_id,body.last_updated";

    // Modelo do caminho da busca no span (sem o id do vendedor).
    private const string SearchEndpoint = "/users/{id}/items/search";

    private MercadoLivreOptions Options => options.Value;

    public async IAsyncEnumerable<IReadOnlyList<string>> SearchItemIdsAsync(
        Guid accountId, string status, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        var mlUserId = await MlUserIdAsync(accountId, cancellationToken);
        var path = string.Create(CultureInfo.InvariantCulture, $"users/{mlUserId}/items/search");

        var pages = ItemSearchPaginator.PaginateAsync(
            (request, token) => FetchSearchPageAsync(accountId, path, status, request, token), cancellationToken);
        await using var enumerator = pages.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (MercadoLivreUnavailableException exception) when (exception.AccountId == Guid.Empty)
            {
                // Falha do paginador (scan que não avança): completa com a conta.
                throw new MercadoLivreUnavailableException(accountId, exception.Message, exception);
            }

            yield return enumerator.Current;
        }
    }

    public async Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(
        Guid accountId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        await MlUserIdAsync(accountId, cancellationToken);
        if (itemIds.Count == 0)
        {
            return [];
        }

        var found = new Dictionary<string, MercadoLivreItemResult>(StringComparer.Ordinal);
        foreach (var batch in itemIds.Distinct(StringComparer.Ordinal).Chunk(Options.BulkBatchSize))
        {
            var query = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ids"] = string.Join(',', batch),
                ["attributes"] = BulkAttributes,
            };
            using var document = await GetJsonAsync(accountId, "/items/bulk", QueryHelpers.AddQueryString("items/bulk", query), cancellationToken);
            foreach (var result in BulkItemMapper.Map(accountId, document.RootElement))
            {
                found[result.ItemId] = result;
            }
        }

        // Um resultado por ID pedido, na ordem pedida; ID ausente da resposta é Unavailable.
        return [.. itemIds.Select(id => found.TryGetValue(id, out var result) ? result : new MercadoLivreItemResult.Unavailable(id))];
    }

    private async Task<ItemSearchPage> FetchSearchPageAsync(
        Guid accountId, string path, string status, ItemSearchPageRequest request, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["status"] = status,
            ["limit"] = ItemSearchPaginator.PageSize.ToString(CultureInfo.InvariantCulture),
        };
        if (request.Scan)
        {
            query["search_type"] = "scan";
            if (request.ScrollId is not null)
            {
                query["scroll_id"] = request.ScrollId;
            }
        }
        else
        {
            query["offset"] = request.Offset.ToString(CultureInfo.InvariantCulture);
        }

        using var document = await GetJsonAsync(accountId, SearchEndpoint, QueryHelpers.AddQueryString(path, query), cancellationToken);
        try
        {
            var root = document.RootElement;
            var results = root.GetProperty("results").EnumerateArray().Select(id => id.GetString() ?? string.Empty)
                .Where(id => id.Length > 0).ToList();
            var total = root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var totalElement)
                ? totalElement.GetInt32()
                : 0;
            var scrollId = root.TryGetProperty("scroll_id", out var scroll) && scroll.ValueKind == JsonValueKind.String
                ? scroll.GetString()
                : null;
            return new ItemSearchPage(results, total, scrollId);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MercadoLivreUnavailableException(accountId, "resposta inválida da busca", exception);
        }
    }

    /// <summary>GET autenticado à API do ML; devolve o JSON ou lança a exceção do contrato.</summary>
    private async Task<JsonDocument> GetJsonAsync(Guid accountId, string endpoint, string relativeUrl, CancellationToken cancellationToken)
    {
        using var activity = MercadoLivreTracing.StartApiCall(endpoint, accountId);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Options.ApiBaseUrl.TrimEnd('/')}/{relativeUrl}"));
        request.Options.Set(MercadoLivreAuthenticationHandler.AccountId, accountId);
        request.Headers.Accept.Add(new("application/json"));

        try
        {
            using var response = await httpClients.CreateClient(MercadoLivreModule.ApiHttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // 401 mesmo depois da renovação forçada (no handler): a conta precisa ser reconectada.
                var rejected = request.Headers.Authorization?.Parameter ?? string.Empty;
                await tokens.RequireReauthorizationAsync(accountId, rejected, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MercadoLivreUnavailableException(
                    accountId, string.Create(CultureInfo.InvariantCulture, $"o Mercado Livre respondeu {(int)response.StatusCode}"));
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
        catch (BrokenCircuitException exception)
        {
            throw new MercadoLivreUnavailableException(accountId, "circuito aberto", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new MercadoLivreUnavailableException(accountId, "falha de rede ou timeout", exception);
        }
        catch (JsonException exception)
        {
            throw new MercadoLivreUnavailableException(accountId, "resposta inválida", exception);
        }
    }

    /// <summary>Id do vendedor da conta; lança se ela não existe ou precisa ser reconectada (sem chamar o ML).</summary>
    private async Task<long> MlUserIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        return MercadoLivreAccessTokenProvider.EnsureUsable(accountId, account).MlUserId;
    }
}
