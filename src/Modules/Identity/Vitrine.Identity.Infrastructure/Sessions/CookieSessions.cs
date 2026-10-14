using Microsoft.AspNetCore.Identity;
using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Infrastructure.Users;

namespace Vitrine.Identity.Infrastructure.Sessions;

/// <summary>Sessão em cookie do ASP.NET Core Identity, com bloqueio após falhas seguidas (ADR-0007).</summary>
internal sealed class CookieSessions(SignInManager<User> signIn, IPasswordHasher<User> hasher, TimeProvider clock) : ISessions
{
    // Hash de uma senha qualquer: e-mail inexistente custa o mesmo tempo que senha errada (não revela contas).
    private static readonly User Nobody = new();
    private static string? _decoyHash;

    public async Task SignInAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await signIn.UserManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("Usuário recém-cadastrado não encontrado.");
        await signIn.SignInAsync(user, isPersistent: true);
    }

    public async Task<LoginResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await signIn.UserManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            _decoyHash ??= hasher.HashPassword(Nobody, "senha-que-nao-existe");
            hasher.VerifyHashedPassword(Nobody, _decoyHash, password ?? string.Empty);
            return new LoginResult.InvalidCredentials();
        }

        var result = await signIn.PasswordSignInAsync(user, password ?? string.Empty, isPersistent: true, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return new LoginResult.SignedIn();
        }

        if (result.IsLockedOut && await signIn.UserManager.GetLockoutEndDateAsync(user) is { } lockoutEnd)
        {
            var retryAfter = lockoutEnd - clock.GetUtcNow();
            return new LoginResult.LockedOut(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1));
        }

        return new LoginResult.InvalidCredentials();
    }

    public Task SignOutAsync() => signIn.SignOutAsync();
}
