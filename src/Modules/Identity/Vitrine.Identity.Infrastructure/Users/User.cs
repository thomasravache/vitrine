using Microsoft.AspNetCore.Identity;

namespace Vitrine.Identity.Infrastructure.Users;

/// <summary>Usuário do ASP.NET Core Identity com o nome informado no cadastro (o e-mail é também o UserName).</summary>
public sealed class User : IdentityUser<Guid>
{
    public string Name { get; set; } = string.Empty;
}
