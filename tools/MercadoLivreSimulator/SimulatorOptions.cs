using System.Globalization;

namespace Vitrine.MercadoLivreSimulator;

/// <summary>
/// Configuração <c>Simulator:*</c> (SPEC-0024, seção 6). Credenciais sempre sintéticas e públicas: o simulador nunca
/// recebe as do app real do DevCenter.
/// </summary>
internal sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Validade padrão do access token (<c>expires_in</c> do ML: 6 h).</summary>
    public static readonly TimeSpan DefaultAccessTokenLifetime = TimeSpan.FromSeconds(21_600);

    /// <summary><c>client_id</c> aceito (numérico, como o <c>application_id</c> do DevCenter).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary><c>client_secret</c> aceito na troca e na renovação de tokens.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary><c>redirect_uri</c> aceitos (comparação exata, como no DevCenter).</summary>
    public IList<string> AllowedRedirectUris { get; } = [];

    /// <summary>URL que recebe as notificações de <c>POST /__sim/notify</c> (webhook da Api).</summary>
    public string? NotificationUrl { get; set; }

    /// <summary>Validade do access token: segundos (ex.: <c>21600</c>) ou <c>hh:mm:ss</c>; padrão 21600 s.</summary>
    public string? AccessTokenLifetime { get; set; }

    /// <summary><see cref="AccessTokenLifetime"/> interpretado; <c>null</c> quando o valor é inválido.</summary>
    public TimeSpan? ParsedAccessTokenLifetime()
    {
        if (string.IsNullOrWhiteSpace(AccessTokenLifetime))
        {
            return DefaultAccessTokenLifetime;
        }

        if (int.TryParse(AccessTokenLifetime, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        return TimeSpan.TryParse(AccessTokenLifetime, CultureInfo.InvariantCulture, out var lifetime) && lifetime > TimeSpan.Zero
            ? lifetime
            : null;
    }

    /// <summary>Problemas de configuração que impedem a subida.</summary>
    public IEnumerable<string> Problems()
    {
        if (!long.TryParse(ClientId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            yield return $"{SectionName}:ClientId precisa ser numérico (é o application_id das notificações).";
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return $"{SectionName}:ClientSecret é obrigatório.";
        }

        if (AllowedRedirectUris.Count == 0 || AllowedRedirectUris.Any(uri => !Uri.TryCreate(uri, UriKind.Absolute, out _)))
        {
            yield return $"{SectionName}:AllowedRedirectUris precisa de ao menos uma URL absoluta.";
        }

        if (!string.IsNullOrWhiteSpace(NotificationUrl) && !Uri.TryCreate(NotificationUrl, UriKind.Absolute, out _))
        {
            yield return $"{SectionName}:NotificationUrl precisa ser uma URL absoluta.";
        }

        if (ParsedAccessTokenLifetime() is null)
        {
            yield return $"{SectionName}:AccessTokenLifetime precisa ser um número de segundos positivo ou hh:mm:ss.";
        }
    }
}
