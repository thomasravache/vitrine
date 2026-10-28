namespace Vitrine.Identity.IntegrationEvents;

/// <summary>
/// Evento de integração <c>WorkspaceCreated</c> v1 (contrato público do módulo Identity — SPEC-0006):
/// publicado pelo outbox na mesma transação do cadastro (ADR-0014).
/// </summary>
/// <param name="WorkspaceId">Workspace criado.</param>
/// <param name="OwnerUserId">Usuário dono (quem se cadastrou).</param>
/// <param name="CreatedAt">Instante da criação.</param>
public sealed record WorkspaceCreated(Guid WorkspaceId, Guid OwnerUserId, DateTimeOffset CreatedAt);
