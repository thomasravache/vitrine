namespace Vitrine.MercadoLivre.Domain.Accounts;

/// <summary>
/// Conta do Mercado Livre conectada a um workspace (SPEC-0020). Scaffold do contrato: o comportamento é entregue
/// na fase Green.
/// </summary>
public sealed class MercadoLivreAccount
{
    private readonly List<IDomainEvent> _domainEvents = [];

    // Materialização pelo EF Core.
    private MercadoLivreAccount()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public long MlUserId { get; private set; }

    public string Nickname { get; private set; } = string.Empty;

    public string SiteId { get; private set; } = string.Empty;

    public MercadoLivreAccountStatus Status { get; private set; }

    public string AccessToken { get; private set; } = string.Empty;

    public string RefreshToken { get; private set; } = string.Empty;

    public DateTimeOffset AccessTokenExpiresAt { get; private set; }

    public DateTimeOffset ConnectedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Eventos de domínio gerados e ainda não publicados.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Descarta os eventos de domínio depois de publicados.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>Conecta uma conta nova ao workspace.</summary>
    public static MercadoLivreAccount Connect(Guid workspaceId, MercadoLivreSeller seller, MercadoLivreTokens tokens, DateTimeOffset now) =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");

    /// <summary>Reconecta a mesma conta no mesmo workspace, trocando os tokens.</summary>
    public void Reconnect(MercadoLivreSeller seller, MercadoLivreTokens tokens, DateTimeOffset now) =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");
}
