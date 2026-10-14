namespace Vitrine.Identity.Application.Abstractions;

/// <summary>Sessão do usuário no navegador (cookie HttpOnly na mesma origem — ADR-0007).</summary>
public interface ISessions
{
    /// <summary>Abre a sessão do usuário recém-cadastrado.</summary>
    Task SignInAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Confere a senha (com bloqueio após falhas seguidas) e, se correta, abre a sessão.</summary>
    Task<LoginResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>Encerra a sessão atual.</summary>
    Task SignOutAsync();
}

/// <summary>Resultado de <see cref="ISessions.SignInWithPasswordAsync"/>.</summary>
public abstract record LoginResult
{
    private LoginResult()
    {
    }

    public sealed record SignedIn : LoginResult;

    /// <summary>E-mail inexistente ou senha errada — indistinguíveis de propósito.</summary>
    public sealed record InvalidCredentials : LoginResult;

    /// <summary>Conta bloqueada por falhas seguidas; pode tentar de novo depois de <paramref name="RetryAfter"/>.</summary>
    public sealed record LockedOut(TimeSpan RetryAfter) : LoginResult;
}
