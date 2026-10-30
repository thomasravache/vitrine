namespace Vitrine.MercadoLivre.IntegrationEvents;

/// <summary>
/// Evento de integração <c>MercadoLivreItemChanged</c> v1 (contrato público do módulo MercadoLivre — SPEC-0023, §6):
/// o ML notificou a alteração de um anúncio da conta (tópico <c>items</c>). Publicado pelo outbox (ADR-0014); o tipo
/// nasce na SPEC-0022 (consumidor), a SPEC-0023 só o publica.
/// </summary>
/// <param name="AccountId">Conta dona do anúncio.</param>
/// <param name="WorkspaceId">Workspace dono da conta.</param>
/// <param name="MlUserId">Id do vendedor no Mercado Livre (int64).</param>
/// <param name="ItemId">Anúncio alterado (ex.: <c>MLB1234567890</c>).</param>
/// <param name="NotificationId"><c>_id</c> da notificação do ML.</param>
/// <param name="OccurredAt">Instante de envio da notificação (<c>sent</c>, UTC).</param>
public sealed record MercadoLivreItemChanged(
    Guid AccountId, Guid WorkspaceId, long MlUserId, string ItemId, string NotificationId, DateTimeOffset OccurredAt);
