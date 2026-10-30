using System.Diagnostics.CodeAnalysis;

namespace Vitrine.MercadoLivre.Application.Notifications;

/// <summary>Interpretação do <c>resource</c> da notificação por tópico (SPEC-0023, §6).</summary>
public static class NotificationResource
{
    /// <summary>
    /// Tópico <c>items</c>: <paramref name="resource"/> no formato <c>^/items/(MLB\d+)$</c> devolve o id do anúncio;
    /// qualquer outro formato é inválido.
    /// </summary>
    public static bool TryGetItemId(string? resource, [NotNullWhen(true)] out string? itemId) =>
        throw new NotImplementedException("SPEC-0023: interpretação do resource do tópico items.");
}
