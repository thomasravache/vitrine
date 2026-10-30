namespace Vitrine.MercadoLivre.IntegrationEvents;

/// <summary>
/// Evento de integração <c>MercadoLivreAccountConnected</c> v1 (contrato público do módulo MercadoLivre — SPEC-0020):
/// publicado pelo outbox na mesma transação que grava a conta (ADR-0014).
/// </summary>
/// <param name="AccountId">Conta conectada.</param>
/// <param name="WorkspaceId">Workspace dono da conta.</param>
/// <param name="MlUserId">Id do vendedor no Mercado Livre (int64).</param>
/// <param name="SiteId">Site do ML (ex.: <c>MLB</c>).</param>
/// <param name="Reconnected">Verdadeiro quando a mesma conta foi conectada de novo no mesmo workspace.</param>
/// <param name="OccurredAt">Instante da conexão (UTC).</param>
public sealed record MercadoLivreAccountConnected(
    Guid AccountId, Guid WorkspaceId, long MlUserId, string SiteId, bool Reconnected, DateTimeOffset OccurredAt);
