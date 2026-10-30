using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Vitrine.MercadoLivre.Application.Notifications;

/// <summary>Interpretação do <c>resource</c> da notificação por tópico (SPEC-0023, §6).</summary>
public static partial class NotificationResource
{
    /// <summary>
    /// Tópico <c>items</c>: <paramref name="resource"/> no formato <c>^/items/(MLB\d+)$</c> devolve o id do anúncio;
    /// qualquer outro formato é inválido. O casamento é exato (<c>\z</c>: nem quebra de linha no fim) e só aceita
    /// dígitos ASCII.
    /// </summary>
    public static bool TryGetItemId(string? resource, [NotNullWhen(true)] out string? itemId)
    {
        itemId = null;
        if (string.IsNullOrEmpty(resource))
        {
            return false;
        }

        var match = ItemResource().Match(resource);
        if (!match.Success)
        {
            return false;
        }

        itemId = match.Groups["item"].Value;
        return true;
    }

    [GeneratedRegex(@"\A/items/(?<item>MLB[0-9]+)\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 100)]
    private static partial Regex ItemResource();
}
