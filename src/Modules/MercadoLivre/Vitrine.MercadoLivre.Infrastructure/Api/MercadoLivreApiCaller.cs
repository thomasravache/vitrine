using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Vitrine.MercadoLivre.Infrastructure.Diagnostics;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Chamadas autenticadas à API do ML pelo cliente <see cref="MercadoLivreModule.ApiHttpClientName"/> para os gateways da
/// SPEC-0033, no padrão do <see cref="MercadoLivreItemsGateway"/> (SPEC-0021): o token é anexado pelo
/// <see cref="MercadoLivreAuthenticationHandler"/> (que também renova e reenvia uma vez depois de um 401), a resiliência
/// é a do pipeline do cliente (sem retry em PUT/POST) e cada chamada gera um span com o caminho modelo.
/// </summary>
internal sealed class MercadoLivreApiCaller(
    IHttpClientFactory httpClients,
    MercadoLivreAccessTokenProvider tokens,
    IOptions<MercadoLivreOptions> options)
{
    /// <summary>
    /// GET que devolve o JSON, ou nulo quando o ML responde 404. Outras falhas lançam
    /// <see cref="MercadoLivreUnavailableException"/>; um 401 que persiste depois da renovação forçada lança
    /// <see cref="MercadoLivreReauthorizationRequiredException"/>.
    /// </summary>
    public async Task<JsonDocument?> GetJsonOrNullAsync(
        Guid accountId, string endpoint, string relativeUrl, CancellationToken cancellationToken)
    {
        using var activity = MercadoLivreTracing.StartApiCall(endpoint, accountId);
        using var request = NewRequest(HttpMethod.Get, accountId, relativeUrl);
        try
        {
            using var response = await SendAsync(accountId, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MercadoLivreUnavailableException(
                    accountId, string.Create(CultureInfo.InvariantCulture, $"o Mercado Livre respondeu {(int)response.StatusCode}"));
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (Unavailability(exception, cancellationToken) is { } reason)
        {
            throw new MercadoLivreUnavailableException(accountId, reason, exception);
        }
        catch (JsonException exception)
        {
            throw new MercadoLivreUnavailableException(accountId, "resposta inválida", exception);
        }
    }

    /// <summary>
    /// Escrita (<c>PUT</c>/<c>POST</c>) com corpo JSON, enviada uma única vez pelo pipeline (o reenvio depois de um 401 é
    /// do handler do token). Devolve o status e o corpo da resposta, ou <see cref="WriteResponse.Unreachable"/> quando o
    /// ML não respondeu (rede, timeout, circuito aberto, renovação indisponível): o resultado da escrita é incerto. Um
    /// 401 que persiste depois da renovação forçada lança <see cref="MercadoLivreReauthorizationRequiredException"/>.
    /// </summary>
    public async Task<WriteResponse> WriteAsync(
        Guid accountId, HttpMethod method, string endpoint, string relativeUrl, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var activity = MercadoLivreTracing.StartApiCall(endpoint, accountId);
        using var request = NewRequest(method, accountId, relativeUrl);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await SendAsync(accountId, request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new WriteResponse.Answered((int)response.StatusCode, content);
        }
        catch (Exception exception) when (Unavailability(exception, cancellationToken) is { } reason)
        {
            return new WriteResponse.Unreachable(reason);
        }
        catch (MercadoLivreUnavailableException exception)
        {
            // Token indisponível (renovação falhou ou conta travada): nada foi enviado.
            return new WriteResponse.Unreachable(exception.Reason.Length > 0 ? exception.Reason : "token indisponível");
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, Guid accountId, string relativeUrl)
    {
        var request = new HttpRequestMessage(method, new Uri($"{options.Value.ApiBaseUrl.TrimEnd('/')}/{relativeUrl}"));
        request.Options.Set(MercadoLivreAuthenticationHandler.AccountId, accountId);
        request.Headers.Accept.Add(new("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(Guid accountId, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await httpClients.CreateClient(MercadoLivreModule.ApiHttpClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // 401 mesmo depois da renovação forçada (no handler): a conta precisa ser reconectada (lança).
            response.Dispose();
            var rejected = request.Headers.Authorization?.Parameter ?? string.Empty;
            await tokens.RequireReauthorizationAsync(accountId, rejected, cancellationToken);
            throw new MercadoLivreReauthorizationRequiredException(accountId);
        }

        return response;
    }

    // Falha de rede, timeout (Http.Resilience ou HttpClient) ou circuito aberto; o cancelamento pedido pelo chamador
    // continua propagando.
    private static string? Unavailability(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        BrokenCircuitException => "circuito aberto",
        HttpRequestException or TimeoutRejectedException => "falha de rede ou timeout",
        OperationCanceledException when !cancellationToken.IsCancellationRequested => "falha de rede ou timeout",
        _ => null,
    };
}

/// <summary>Resposta de uma escrita no ML.</summary>
internal abstract record WriteResponse
{
    /// <summary>O ML respondeu (qualquer status exceto 401, tratado pelo handler do token).</summary>
    public sealed record Answered(int StatusCode, string Body) : WriteResponse;

    /// <summary>O ML não respondeu: o resultado da escrita é incerto.</summary>
    public sealed record Unreachable(string Reason) : WriteResponse;
}
