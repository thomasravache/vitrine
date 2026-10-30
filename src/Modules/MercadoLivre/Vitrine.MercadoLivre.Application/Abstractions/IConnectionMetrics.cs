namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>Contador <c>vitrine.mercadolivre.connections</c> por resultado (Plano de Rollout da SPEC-0020).</summary>
public interface IConnectionMetrics
{
    void Record(ConnectionOutcome outcome);
}

/// <summary>Resultado de uma conclusão de conexão (tag <c>result</c>).</summary>
public enum ConnectionOutcome
{
    Connected,
    Reconnected,
    StateInvalid,
    CodeInvalid,
    MlUnavailable,
    SiteNotSupported,
    InUse,
}
