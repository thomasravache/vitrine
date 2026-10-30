namespace Vitrine.MercadoLivre.Application.Abstractions;

/// <summary>Contador <c>vitrine.mercadolivre.notifications{topic,result}</c> (Plano de Rollout da SPEC-0023).</summary>
public interface INotificationMetrics
{
    /// <summary>Uma notificação do <paramref name="topic"/> (nulo quando o corpo não foi lido) terminou em <paramref name="result"/>.</summary>
    void Record(string? topic, NotificationResult result);
}

/// <summary>
/// Resultado de uma notificação (tag <c>result</c>; emenda v1.1): <see cref="Accepted"/>, <see cref="Duplicate"/>,
/// <see cref="IgnoredApp"/> e <see cref="IgnoredTopic"/> no recebimento; <see cref="Invalid"/> no 400 e no processamento de
/// resource inválido; <see cref="UnknownUser"/> no processamento.
/// </summary>
public enum NotificationResult
{
    Accepted,
    Duplicate,
    IgnoredApp,
    IgnoredTopic,
    UnknownUser,
    Invalid,
}
