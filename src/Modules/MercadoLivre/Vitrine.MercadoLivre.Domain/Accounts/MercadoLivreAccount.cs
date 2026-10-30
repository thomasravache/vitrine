namespace Vitrine.MercadoLivre.Domain.Accounts;

/// <summary>
/// Conta do Mercado Livre conectada a um workspace (SPEC-0020): guarda só o id, o apelido e o site do vendedor, e os
/// tokens (cifrados em repouso pela persistência; nunca saem da API).
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
    public static MercadoLivreAccount Connect(Guid workspaceId, MercadoLivreSeller seller, MercadoLivreTokens tokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(tokens);

        var account = new MercadoLivreAccount
        {
            Id = Guid.CreateVersion7(now),
            WorkspaceId = workspaceId,
            MlUserId = seller.MlUserId,
            ConnectedAt = now,
        };
        account.Apply(seller, tokens, now, reconnected: false);
        return account;
    }

    /// <summary>Reconecta a mesma conta no mesmo workspace, trocando os tokens.</summary>
    public void Reconnect(MercadoLivreSeller seller, MercadoLivreTokens tokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(tokens);
        if (seller.MlUserId != MlUserId)
        {
            throw new InvalidOperationException("Reconexão só da mesma conta do Mercado Livre.");
        }

        Apply(seller, tokens, now, reconnected: true);
    }

    private void Apply(MercadoLivreSeller seller, MercadoLivreTokens tokens, DateTimeOffset now, bool reconnected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens.RefreshToken);

        Nickname = seller.Nickname;
        SiteId = seller.SiteId;
        Status = MercadoLivreAccountStatus.Connected;
        AccessToken = tokens.AccessToken;
        RefreshToken = tokens.RefreshToken;
        AccessTokenExpiresAt = now + tokens.ExpiresIn;
        UpdatedAt = now;
        _domainEvents.Add(new MercadoLivreAccountConnectedDomainEvent(Id, WorkspaceId, MlUserId, SiteId, reconnected, now));
    }
}
