using System.Diagnostics.Metrics;
using Vitrine.Identity.Application.Abstractions;

namespace Vitrine.Identity.Infrastructure.Diagnostics;

/// <summary>
/// Métricas da jornada de acesso no meter <see cref="IdentityModule.MeterName"/> (contrato da SPEC-0007). Sem dados
/// pessoais nas tags (ADR-0006): só o resultado da tentativa.
/// </summary>
internal sealed class IdentityMetrics
{
    private const string ResultTag = "result";

    private readonly Counter<long> _signups;
    private readonly Counter<long> _logins;
    private readonly Counter<long> _lockouts;

    public IdentityMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(IdentityModule.MeterName);
        _signups = meter.CreateCounter<long>("vitrine.auth.signups", "{signup}", "Cadastros concluídos (conta e workspace criados).");
        _logins = meter.CreateCounter<long>("vitrine.auth.logins", "{login}", "Tentativas de login por resultado (success, failure, locked).");
        _lockouts = meter.CreateCounter<long>("vitrine.auth.lockouts", "{lockout}", "Contas bloqueadas por falhas seguidas de login.");
    }

    /// <summary>Um cadastro foi concluído.</summary>
    public void SignupCompleted() => _signups.Add(1);

    /// <summary>Uma tentativa de login terminou com <paramref name="result"/>; <paramref name="lockedNow"/> = esta tentativa bloqueou a conta.</summary>
    public void LoginAttempted(LoginResult result, bool lockedNow)
    {
        var outcome = result switch
        {
            LoginResult.SignedIn => "success",
            LoginResult.LockedOut => "locked",
            _ => "failure",
        };
        _logins.Add(1, new KeyValuePair<string, object?>(ResultTag, outcome));

        if (lockedNow)
        {
            _lockouts.Add(1);
        }
    }
}
