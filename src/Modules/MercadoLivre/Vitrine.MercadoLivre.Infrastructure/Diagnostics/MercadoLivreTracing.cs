using System.Diagnostics;

namespace Vitrine.MercadoLivre.Infrastructure.Diagnostics;

/// <summary>
/// Spans do gateway da API do ML no <see cref="ActivitySource"/> <see cref="MercadoLivreModule.ActivitySourceName"/> (o
/// host o assina no OpenTelemetry): um por chamada, com <c>ml.endpoint</c> (modelo do caminho, sem query nem IDs) e
/// <c>ml.account_id</c>. Nunca tokens.
/// </summary>
internal static class MercadoLivreTracing
{
    public const string EndpointTag = "ml.endpoint";
    public const string AccountIdTag = "ml.account_id";

    private static readonly ActivitySource Source = new(MercadoLivreModule.ActivitySourceName);

    public static Activity? StartApiCall(string endpoint, Guid accountId)
    {
        var activity = Source.StartActivity($"mercadolivre {endpoint}", ActivityKind.Internal);
        activity?.SetTag(EndpointTag, endpoint);
        activity?.SetTag(AccountIdTag, accountId.ToString("D"));
        return activity;
    }
}
