using Vitrine.Identity.Application.Register;

namespace Vitrine.UnitTests.Identity;

public sealed class RegisterCommandValidatorTests
{
    private const string ValidName = "Marina Souza";
    private const string ValidEmail = "marina@example.com";
    private const string ValidPassword = "senha-forte-10"; // 14 caracteres

    [Fact]
    [Trait("spec", "SPEC-0006:UT-02")]
    public void Comando_valido_nao_gera_erros()
    {
        // Dado um cadastro com nome, e-mail e senha válidos; quando validado
        var errors = RegisterCommandValidator.Validate(new RegisterCommand(ValidName, ValidEmail, ValidPassword));

        // Então não há erros
        Assert.Empty(errors);
    }

    [Fact]
    [Trait("spec", "SPEC-0006:UT-02")]
    public void Nome_vazio_email_invalido_e_senha_de_9_caracteres_geram_um_erro_por_campo()
    {
        // Dado um cadastro com nome vazio, e-mail inválido e senha com 9 caracteres
        var command = new RegisterCommand(string.Empty, "nao-e-email", "123456789");

        // Quando validado
        var errors = RegisterCommandValidator.Validate(command);

        // Então cada campo inválido gera seu erro (e só eles)
        Assert.Equal(["email", "name", "password"], errors.Keys.Order(StringComparer.Ordinal));
        Assert.All(errors, field => Assert.True(
            field.Value.Length > 0 && field.Value.All(message => !string.IsNullOrWhiteSpace(message)),
            $"O campo '{field.Key}' precisa de ao menos uma mensagem de erro."));
    }

    [Theory]
    [Trait("spec", "SPEC-0006:UT-02")]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("M", "name")]
    public void Nome_fora_de_2_a_100_caracteres_gera_erro_so_no_campo_name(string name, string field) =>
        AssertOnlyFieldInvalid(new RegisterCommand(name, ValidEmail, ValidPassword), field);

    [Fact]
    [Trait("spec", "SPEC-0006:UT-02")]
    public void Nome_com_101_caracteres_gera_erro_so_no_campo_name() =>
        AssertOnlyFieldInvalid(new RegisterCommand(new string('a', 101), ValidEmail, ValidPassword), "name");

    [Theory]
    [Trait("spec", "SPEC-0006:UT-02")]
    [InlineData("")]
    [InlineData("nao-e-email")]
    [InlineData("marina@")]
    [InlineData("@example.com")]
    public void Email_invalido_gera_erro_so_no_campo_email(string email) =>
        AssertOnlyFieldInvalid(new RegisterCommand(ValidName, email, ValidPassword), "email");

    [Theory]
    [Trait("spec", "SPEC-0006:UT-02")]
    [InlineData("")]
    [InlineData("123456789")]
    public void Senha_com_menos_de_10_caracteres_gera_erro_so_no_campo_password(string password) =>
        AssertOnlyFieldInvalid(new RegisterCommand(ValidName, ValidEmail, password), "password");

    [Fact]
    [Trait("spec", "SPEC-0006:UT-02")]
    public void Limites_validos_nome_com_2_e_100_caracteres_e_senha_com_10_nao_geram_erros()
    {
        // Dado os limites inclusivos do contrato (nome 2..100, senha >= 10); quando validados; então não há erros
        Assert.Empty(RegisterCommandValidator.Validate(new RegisterCommand("Bo", ValidEmail, "1234567890")));
        Assert.Empty(RegisterCommandValidator.Validate(new RegisterCommand(new string('a', 100), ValidEmail, ValidPassword)));
    }

    private static void AssertOnlyFieldInvalid(RegisterCommand command, string field)
    {
        // Quando validado
        var errors = RegisterCommandValidator.Validate(command);

        // Então só o campo inválido tem erro, com mensagem
        Assert.Equal([field], errors.Keys);
        Assert.NotEmpty(errors[field]);
        Assert.All(errors[field], message => Assert.False(string.IsNullOrWhiteSpace(message)));
    }
}
