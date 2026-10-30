namespace Vitrine.MercadoLivre.Infrastructure;

/// <summary>Configuração <c>MercadoLivre:*</c> (contrato da SPEC-0020), lida de <c>MercadoLivre__*</c> no ambiente.</summary>
public sealed class MercadoLivreOptions
{
    public const string SectionName = "MercadoLivre";

    /// <summary>Página de autorização oficial do ML Brasil.</summary>
    public const string OfficialAuthorizationBaseUrl = "https://auth.mercadolivre.com.br";

    /// <summary>API oficial do ML.</summary>
    public const string OfficialApiBaseUrl = "https://api.mercadolibre.com";

    public string? ClientId { get; set; }

    /// <summary>Segredo do app no DevCenter (secret do Environment; nunca em log).</summary>
    public string? ClientSecret { get; set; }

    public string? RedirectUri { get; set; }

    public string AuthorizationBaseUrl { get; set; } = OfficialAuthorizationBaseUrl;

    public string ApiBaseUrl { get; set; } = OfficialApiBaseUrl;

    /// <summary>Em Production, só com <c>true</c> URLs diferentes das oficiais são aceitas (ex.: simulador — SPEC-0024).</summary>
    public bool AllowNonOfficialEndpoints { get; set; }
}
