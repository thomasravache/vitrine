namespace Vitrine.Identity.Application.Register;

/// <summary>
/// Regras de entrada do cadastro (contrato da SPEC-0006): nome 2..100, e-mail válido, senha com 10+ caracteres.
/// </summary>
public static class RegisterCommandValidator
{
    /// <summary>
    /// Valida o comando e devolve os erros por campo (<c>name</c>, <c>email</c>, <c>password</c>);
    /// vazio quando o comando é válido.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(RegisterCommand command) =>
        throw new NotImplementedException("SPEC-0006: validação do cadastro ainda não implementada.");
}
