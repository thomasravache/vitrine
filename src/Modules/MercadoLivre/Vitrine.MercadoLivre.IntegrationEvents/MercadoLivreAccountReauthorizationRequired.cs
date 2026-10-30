namespace Vitrine.MercadoLivre.IntegrationEvents;

/// <summary>
/// Evento de integração <c>MercadoLivreAccountReauthorizationRequired</c> v1 (contrato público do módulo MercadoLivre —
/// SPEC-0021): o ML recusou o refresh token e a conta ficou em <c>reauthorization_required</c>, com os tokens apagados.
/// Publicado pelo outbox na mesma transação que muda a conta (ADR-0014).
/// </summary>
/// <param name="AccountId">Conta que precisa ser reconectada.</param>
/// <param name="WorkspaceId">Workspace dono da conta.</param>
/// <param name="MlUserId">Id do vendedor no Mercado Livre (int64).</param>
/// <param name="OccurredAt">Instante da mudança (UTC).</param>
public sealed record MercadoLivreAccountReauthorizationRequired(Guid AccountId, Guid WorkspaceId, long MlUserId, DateTimeOffset OccurredAt);
