namespace Vitrine.MercadoLivre.Domain.Accounts;

/// <summary>Situação da conta conectada (contrato da SPEC-0020: <c>connected</c> | <c>reauthorization_required</c>).</summary>
public enum MercadoLivreAccountStatus
{
    /// <summary>Tokens válidos: o Vitrine age em nome do vendedor.</summary>
    Connected,

    /// <summary>O ML recusou o refresh token: o Owner precisa conectar de novo (SPEC-0021).</summary>
    ReauthorizationRequired,
}
