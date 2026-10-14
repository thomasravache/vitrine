using Microsoft.AspNetCore.Identity;
using Vitrine.Identity.Application.Abstractions;

namespace Vitrine.Identity.Infrastructure.Users;

internal sealed class UserAccounts(UserManager<User> users) : IUserAccounts
{
    private static readonly HashSet<string> DuplicateCodes = new(StringComparer.Ordinal)
    {
        nameof(IdentityErrorDescriber.DuplicateEmail),
        nameof(IdentityErrorDescriber.DuplicateUserName),
    };

    public async Task<UserCreation> CreateAsync(string name, string email, string password, CancellationToken cancellationToken)
    {
        var user = new User { Id = Guid.CreateVersion7(), Name = name, UserName = email, Email = email };
        var result = await users.CreateAsync(user, password);
        if (result.Succeeded)
        {
            return new UserCreation.Created(user.Id);
        }

        if (result.Errors.Any(error => DuplicateCodes.Contains(error.Code)))
        {
            return new UserCreation.EmailTaken();
        }

        // Demais recusas do Identity (formato de e-mail, política de senha) viram erro no campo correspondente.
        return new UserCreation.Rejected(result.Errors
            .GroupBy(error => error.Code.Contains("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray(), StringComparer.Ordinal));
    }
}
