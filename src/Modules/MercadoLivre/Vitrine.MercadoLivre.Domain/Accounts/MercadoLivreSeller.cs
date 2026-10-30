namespace Vitrine.MercadoLivre.Domain.Accounts;

/// <summary>Vendedor lido em <c>GET /users/me</c>: só o que o Vitrine guarda (sem e-mail, nome nem documento).</summary>
/// <param name="MlUserId">Id do vendedor no ML (int64).</param>
/// <param name="Nickname">Apelido público do vendedor.</param>
/// <param name="SiteId">Site do ML (ex.: <c>MLB</c>).</param>
public sealed record MercadoLivreSeller(long MlUserId, string Nickname, string SiteId);
