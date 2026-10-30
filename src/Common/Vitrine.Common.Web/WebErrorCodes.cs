namespace Vitrine.Common.Web;

/// <summary>Valores do campo <c>code</c> dos erros (application/problem+json) emitidos pelos filtros comuns.</summary>
public static class WebErrorCodes
{
    /// <summary>Token antiforgery ausente ou inválido (contrato da SPEC-0006, reutilizado pelos módulos).</summary>
    public const string AntiforgeryInvalid = "ANTIFORGERY_INVALID";
}
