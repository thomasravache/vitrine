using System.Text.RegularExpressions;

namespace Vitrine.Identity.Application.Register;

/// <summary>
/// Regras de entrada do cadastro (contrato da SPEC-0006): nome 2..100, e-mail válido, senha com 10+ caracteres.
/// </summary>
public static partial class RegisterCommandValidator
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 254;
    public const int PasswordMinLength = 10;

    /// <summary>
    /// Valida o comando e devolve os erros por campo (<c>name</c>, <c>email</c>, <c>password</c>);
    /// vazio quando o comando é válido.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(RegisterCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            errors["name"] = [$"Informe seu nome (de {NameMinLength} a {NameMaxLength} caracteres)."];
        }

        var email = command.Email?.Trim() ?? string.Empty;
        if (email.Length > EmailMaxLength || !EmailFormat().IsMatch(email))
        {
            errors["email"] = ["Informe um e-mail válido."];
        }

        if ((command.Password ?? string.Empty).Length < PasswordMinLength)
        {
            errors["password"] = [$"A senha precisa ter pelo menos {PasswordMinLength} caracteres."];
        }

        return errors;
    }

    // Formato prático (algo@dominio.tld), sem espaços; a caixa postal é confirmada em outra spec.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailFormat();
}
