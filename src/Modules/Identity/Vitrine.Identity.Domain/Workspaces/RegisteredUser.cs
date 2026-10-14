namespace Vitrine.Identity.Domain.Workspaces;

/// <summary>Usuário recém-cadastrado, dono do workspace criado no cadastro.</summary>
/// <param name="Id">Identificador do usuário.</param>
/// <param name="Name">Nome informado no cadastro.</param>
public sealed record RegisteredUser(Guid Id, string Name);
