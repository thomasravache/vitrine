namespace Vitrine.MercadoLivre.Infrastructure;

/// <summary>Registro do módulo MercadoLivre no host (scaffold do contrato da SPEC-0020).</summary>
public static class MercadoLivreModule
{
    /// <summary>Nome do <c>HttpClient</c> que fala com a API do ML (<c>ApiBaseUrl</c>), com Http.Resilience.</summary>
    public const string HttpClientName = "mercadolivre";

    /// <summary>Meter das métricas do módulo (<c>vitrine.mercadolivre.*</c>).</summary>
    public const string MeterName = "Vitrine.MercadoLivre";

    /// <summary>Propósito do <c>IFieldProtector</c> para tokens e verifier (SPEC-0008).</summary>
    public const string TokensProtectionPurpose = "mercadolivre.tokens";
}
