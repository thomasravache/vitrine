namespace Vitrine.MercadoLivre.IntegrationEvents;

/// <summary>
/// Evento de integração <c>MercadoLivreAccountDisconnected</c> v1 (contrato público do módulo MercadoLivre — SPEC-0020):
/// publicado pelo outbox na mesma transação que apaga a conta e os tokens (ADR-0014).
/// </summary>
/// <param name="AccountId">Conta desconectada.</param>
/// <param name="WorkspaceId">Workspace dono da conta.</param>
/// <param name="MlUserId">Id do vendedor no Mercado Livre (int64).</param>
/// <param name="OccurredAt">Instante da desconexão (UTC).</param>
public sealed record MercadoLivreAccountDisconnected(Guid AccountId, Guid WorkspaceId, long MlUserId, DateTimeOffset OccurredAt);
