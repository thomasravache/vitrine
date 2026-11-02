using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.MercadoLivre.Infrastructure.Api;

/// <summary>
/// Traduz a resposta de erro de uma escrita no ML (<c>{ message, error, status, cause: [{ code, message }] }</c>) para os
/// códigos estáveis do contrato da SPEC-0033. Esqueleto do Red: a implementação vem no Green.
/// </summary>
internal static class MercadoLivreErrorMapper
{
    /// <summary>Resultado <see cref="FieldResult.Rejected"/> para o <paramref name="statusCode"/> e o corpo cru do ML.</summary>
    public static FieldResult.Rejected Map(int statusCode, string? body) =>
        throw new NotImplementedException("SPEC-0033: MercadoLivreErrorMapper.Map ainda não implementado.");
}
