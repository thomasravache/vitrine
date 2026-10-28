namespace Vitrine.Identity.Application.Register;

/// <summary>Resultado do cadastro.</summary>
public abstract record RegisterResult
{
    private RegisterResult()
    {
    }

    /// <summary>Usuário e workspace criados; <c>WorkspaceCreated</c> está no outbox.</summary>
    public sealed record Registered(Guid UserId, Guid WorkspaceId) : RegisterResult;

    /// <summary>Entrada inválida: erros por campo (<c>name</c>, <c>email</c>, <c>password</c>).</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RegisterResult;

    /// <summary>O e-mail já pertence a outra conta.</summary>
    public sealed record EmailTaken : RegisterResult;
}
