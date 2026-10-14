using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vitrine.Identity.Infrastructure;

namespace Vitrine.UnitTests.Identity;

public sealed class LockoutConfigurationTests
{
    [Fact]
    [Trait("spec", "SPEC-0006:UT-03")]
    public void Configuracao_do_Identity_bloqueia_apos_5_falhas_por_15_minutos()
    {
        // Dado o módulo Identity registrado com a configuração do host (sem acesso ao banco)
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:vitrine"] = "Host=localhost;Database=vitrine_unit;Username=unit;Password=unit",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityModule(configuration);
        using var provider = services.BuildServiceProvider();

        // Quando a configuração do Identity é lida
        var options = provider.GetService<IOptions<IdentityOptions>>();
        Assert.True(options is not null, "AddIdentityModule não registrou o ASP.NET Core Identity (IOptions<IdentityOptions>).");
        var lockout = options.Value.Lockout;

        // Então o bloqueio vale para novos usuários, após 5 falhas, por 15 minutos
        Assert.True(lockout.AllowedForNewUsers, "Bloqueio precisa valer para novos usuários.");
        Assert.Equal(5, lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), lockout.DefaultLockoutTimeSpan);
    }
}
