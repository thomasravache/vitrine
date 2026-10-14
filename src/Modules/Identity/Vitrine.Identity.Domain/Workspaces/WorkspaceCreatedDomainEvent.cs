namespace Vitrine.Identity.Domain.Workspaces;

/// <summary>Evento de domínio: um workspace foi criado para o dono informado.</summary>
public sealed record WorkspaceCreatedDomainEvent(Guid WorkspaceId, Guid OwnerUserId, DateTimeOffset CreatedAt) : IDomainEvent;
