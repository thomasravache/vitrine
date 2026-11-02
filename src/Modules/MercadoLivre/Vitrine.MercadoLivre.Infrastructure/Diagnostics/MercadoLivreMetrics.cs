using System.Diagnostics.Metrics;
using Vitrine.MercadoLivre.Application.Abstractions;

namespace Vitrine.MercadoLivre.Infrastructure.Diagnostics;

/// <summary>
/// Métricas do módulo no meter <see cref="MercadoLivreModule.MeterName"/>: <c>vitrine.mercadolivre.connections</c> por
/// <c>result</c>, <c>vitrine.mercadolivre.token_refreshes</c> por <c>result</c> e
/// <c>vitrine.mercadolivre.notifications</c> por <c>topic</c> e <c>result</c> (SPEC-0023). Sem dados pessoais nem ids nas
/// tags (ADR-0006); o tópico vem de um corpo não confiável, então só <c>items</c> é nomeado (o resto é <c>other</c>).
/// <c>vitrine.mercadolivre.listing_edits</c> por <c>field</c>, <c>result</c> e <c>code</c> (SPEC-0033): o código é um
/// dos códigos estáveis do contrato, nunca texto do ML.
/// </summary>
internal sealed class MercadoLivreMetrics : IConnectionMetrics, INotificationMetrics
{
    private readonly Counter<long> _connections;
    private readonly Counter<long> _tokenRefreshes;
    private readonly Counter<long> _notifications;
    private readonly Counter<long> _listingEdits;

    public MercadoLivreMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MercadoLivreModule.MeterName);
        _connections = meter.CreateCounter<long>(
            "vitrine.mercadolivre.connections", "{connection}", "Conclusões de conexão de conta do Mercado Livre por resultado.");
        _tokenRefreshes = meter.CreateCounter<long>(
            "vitrine.mercadolivre.token_refreshes", "{refresh}", "Renovações do access token do Mercado Livre por resultado.");
        _notifications = meter.CreateCounter<long>(
            "vitrine.mercadolivre.notifications", "{notification}", "Notificações do Mercado Livre por tópico e resultado.");
        _listingEdits = meter.CreateCounter<long>(
            "vitrine.mercadolivre.listing_edits", "{edit}", "Edições de anúncio no Mercado Livre por campo, resultado e código.");
    }

    public void Record(string? topic, NotificationResult result) =>
        _notifications.Add(
            1,
            new KeyValuePair<string, object?>("topic", topic switch
            {
                null => "unknown",
                "items" => "items",
                _ => "other",
            }),
            new KeyValuePair<string, object?>("result", result switch
            {
                NotificationResult.Accepted => "accepted",
                NotificationResult.Duplicate => "duplicate",
                NotificationResult.IgnoredApp => "ignored_app",
                NotificationResult.IgnoredTopic => "ignored_topic",
                NotificationResult.UnknownUser => "unknown_user",
                _ => "invalid",
            }));

    /// <summary>
    /// Um campo da edição de anúncio (<c>title</c> | <c>description</c> | <c>attributes</c>) terminou com
    /// <paramref name="result"/> (<c>applied</c> | <c>rejected</c> | <c>not_attempted</c>) e o código estável do contrato
    /// (<c>none</c> quando aplicado).
    /// </summary>
    public void ListingEdited(string field, string result, string code) =>
        _listingEdits.Add(
            1,
            new KeyValuePair<string, object?>("field", field),
            new KeyValuePair<string, object?>("result", result),
            new KeyValuePair<string, object?>("code", code));

    /// <summary>Uma renovação terminou com <paramref name="outcome"/>.</summary>
    public void TokenRefreshed(TokenRefreshOutcome outcome) =>
        _tokenRefreshes.Add(1, new KeyValuePair<string, object?>("result", outcome switch
        {
            TokenRefreshOutcome.Renewed => "renewed",
            TokenRefreshOutcome.ReauthorizationRequired => "reauthorization_required",
            _ => "unavailable",
        }));

    public void Record(ConnectionOutcome outcome) =>
        _connections.Add(1, new KeyValuePair<string, object?>("result", outcome switch
        {
            ConnectionOutcome.Connected => "connected",
            ConnectionOutcome.Reconnected => "reconnected",
            ConnectionOutcome.StateInvalid => "state_invalid",
            ConnectionOutcome.CodeInvalid => "code_invalid",
            ConnectionOutcome.SiteNotSupported => "site_not_supported",
            ConnectionOutcome.InUse => "in_use",
            _ => "ml_unavailable",
        }));
}

/// <summary>Resultado de uma renovação do access token (tag <c>result</c> de <c>vitrine.mercadolivre.token_refreshes</c>).</summary>
internal enum TokenRefreshOutcome
{
    Renewed,
    ReauthorizationRequired,
    Unavailable,
}
