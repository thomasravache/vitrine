namespace Vitrine.MercadoLivre.Domain.Authorizations;

/// <summary>
/// Autorização OAuth pendente (SPEC-0020): <c>state</c> guardado só como hash, <c>code_verifier</c> cifrado, válida por
/// <see cref="Lifetime"/> e de uso único, amarrada ao usuário e ao workspace que a iniciaram. Scaffold do contrato.
/// </summary>
public sealed class PendingAuthorization
{
    /// <summary>Validade da autorização pendente.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // Materialização pelo EF Core.
    private PendingAuthorization()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string StateHash { get; private set; } = string.Empty;

    public string CodeVerifier { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Cria a autorização pendente iniciada agora pelo usuário no workspace.</summary>
    public static PendingAuthorization Create(Guid workspaceId, Guid userId, string stateHash, string codeVerifier, DateTimeOffset now) =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");

    /// <summary>Tenta consumir a autorização em nome de <paramref name="userId"/>; só a primeira, válida e do mesmo usuário, é aceita.</summary>
    public PendingAuthorizationConsumption Consume(Guid userId, DateTimeOffset now) =>
        throw new NotImplementedException("SPEC-0020: implementado na fase Green.");
}

/// <summary>Resultado de <see cref="PendingAuthorization.Consume"/>.</summary>
public enum PendingAuthorizationConsumption
{
    /// <summary>Primeira consumação, dentro da validade, pelo usuário que iniciou: segue para a troca do code.</summary>
    Accepted,

    /// <summary>Validade de 10 minutos vencida.</summary>
    Expired,

    /// <summary>Já consumida antes (um <c>state</c> nunca serve duas vezes).</summary>
    AlreadyUsed,

    /// <summary>Iniciada por outro usuário.</summary>
    OtherUser,
}
