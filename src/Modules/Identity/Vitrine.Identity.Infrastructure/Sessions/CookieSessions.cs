using Microsoft.AspNetCore.Identity;
using Vitrine.Identity.Application.Abstractions;
using Vitrine.Identity.Infrastructure.Diagnostics;
using Vitrine.Identity.Infrastructure.Users;

namespace Vitrine.Identity.Infrastructure.Sessions;

/// <summary>
/// Sessão em cookie do ASP.NET Core Identity, com bloqueio após falhas seguidas (ADR-0007). Mede cadastros, logins
/// por resultado e bloqueios (SPEC-0007) onde o resultado é conhecido.
/// </summary>
internal sealed class CookieSessions(
    SignInManager<User> signIn, IPasswordHasher<User> hasher, TimeProvider clock, IdentityMetrics metrics) : ISessions
{
    // Hash de uma senha qualquer: e-mail inexistente custa o mesmo tempo que senha errada (não revela contas).
    private static readonly User Nobody = new();
    private static string? _decoyHash;

    public async Task SignInAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await signIn.UserManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("Usuário recém-cadastrado não encontrado.");
        await signIn.SignInAsync(user, isPersistent: true);

        // Só o cadastro abre sessão por este caminho (contrato de ISessions): a conta já foi confirmada no banco.
        metrics.SignupCompleted();
    }

    public async Task<LoginResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var (result, lockedNow) = await CheckPasswordAsync(email, password);
        metrics.LoginAttempted(result, lockedNow);
        return result;
    }

    public Task SignOutAsync() => signIn.SignOutAsync();

    private async Task<(LoginResult Result, bool LockedNow)> CheckPasswordAsync(string email, string password)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await signIn.UserManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            _decoyHash ??= hasher.HashPassword(Nobody, "senha-que-nao-existe");
            hasher.VerifyHashedPassword(Nobody, _decoyHash, password ?? string.Empty);
            return (new LoginResult.InvalidCredentials(), false);
        }

        // Já bloqueada antes da tentativa? Distingue o bloqueio causado por esta tentativa (métrica de lockouts).
        var wasLockedOut = await signIn.UserManager.IsLockedOutAsync(user);
        var result = await signIn.PasswordSignInAsync(user, password ?? string.Empty, isPersistent: true, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return (new LoginResult.SignedIn(), false);
        }

        if (result.IsLockedOut && await signIn.UserManager.GetLockoutEndDateAsync(user) is { } lockoutEnd)
        {
            var retryAfter = lockoutEnd - clock.GetUtcNow();
            return (new LoginResult.LockedOut(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1)), !wasLockedOut);
        }

        return (new LoginResult.InvalidCredentials(), false);
    }
}
