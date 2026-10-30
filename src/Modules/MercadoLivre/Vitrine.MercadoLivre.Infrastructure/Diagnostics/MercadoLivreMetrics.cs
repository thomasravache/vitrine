using System.Diagnostics.Metrics;
using Vitrine.MercadoLivre.Application.Abstractions;

namespace Vitrine.MercadoLivre.Infrastructure.Diagnostics;

/// <summary>
/// Métricas do módulo no meter <see cref="MercadoLivreModule.MeterName"/>: <c>vitrine.mercadolivre.connections</c> por
/// <c>result</c>. Sem dados pessoais nem ids nas tags (ADR-0006).
/// </summary>
internal sealed class MercadoLivreMetrics : IConnectionMetrics
{
    private readonly Counter<long> _connections;

    public MercadoLivreMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MercadoLivreModule.MeterName);
        _connections = meter.CreateCounter<long>(
            "vitrine.mercadolivre.connections", "{connection}", "Conclusões de conexão de conta do Mercado Livre por resultado.");
    }

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
