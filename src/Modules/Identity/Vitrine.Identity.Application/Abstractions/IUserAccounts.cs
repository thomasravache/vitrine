namespace Vitrine.Identity.Application.Abstractions;

/// <summary>Contas de usuário (implementadas com o ASP.NET Core Identity — ADR-0007).</summary>
public interface IUserAccounts
{
    /// <summary>Cria a conta com a senha informada (guardada só como hash).</summary>
    Task<UserCreation> CreateAsync(string name, string email, string password, CancellationToken cancellationToken);
}

/// <summary>Resultado de <see cref="IUserAccounts.CreateAsync"/>.</summary>
public abstract record UserCreation
{
    private UserCreation()
    {
    }

    public sealed record Created(Guid UserId) : UserCreation;

    public sealed record EmailTaken : UserCreation;

    /// <summary>Recusada pelas regras do provedor de identidade, com erros por campo.</summary>
    public sealed record Rejected(IReadOnlyDictionary<string, string[]> Errors) : UserCreation;
}
