namespace Vitrine.MercadoLivre.Domain.Accounts;

/// <summary>Evento de domínio: a conta foi conectada (ou reconectada) ao workspace.</summary>
public sealed record MercadoLivreAccountConnectedDomainEvent(
    Guid AccountId, Guid WorkspaceId, long MlUserId, string SiteId, bool Reconnected, DateTimeOffset OccurredAt) : IDomainEvent;
