namespace Vitrine.Identity.Application.Register;

/// <summary>Cadastro de vendedor: cria usuário e workspace (SPEC-0006).</summary>
public sealed record RegisterCommand(string Name, string Email, string Password);
