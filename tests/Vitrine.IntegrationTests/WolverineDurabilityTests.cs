using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Support;
using Wolverine;

namespace Vitrine.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class WolverineDurabilityTests(PostgresFixture postgres)
{
    private const string ProbeQueue = "durability-probe";

    [Fact]
    [Trait("spec", "SPEC-0003:IT-02")]
    public async Task Mensagem_duravel_pendente_quando_o_host_para_e_processada_exatamente_uma_vez_apos_reinicio()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var probe = new DurabilityProbe(Guid.NewGuid());

        // Dado o host da Api com Wolverine persistindo no PostgreSQL;
        // quando a mensagem é publicada (para processar em 5 s) e o host para antes de processá-la
        await using (var first = CreateHost(connectionString))
        {
            using var scope = first.Services.CreateScope();
            var bus = scope.ServiceProvider.GetService<IMessageBus>();
            Assert.True(bus is not null, "Wolverine não está configurado no host da Api (IMessageBus não registrado).");
            await bus.ScheduleAsync(probe, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, DurabilityProbeHandler.TimesHandled(probe.Id));

        // E o host é reiniciado sobre o mesmo banco
        await using var second = CreateHost(connectionString);
        _ = second.Services;

        // Então o handler processa a mensagem exatamente uma vez
        await Eventually.TrueAsync(
            () => DurabilityProbeHandler.TimesHandled(probe.Id) > 0,
            TimeSpan.FromSeconds(60),
            "a mensagem persistida não foi processada após o reinício do host",
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); // janela para detectar reprocessamento
        Assert.Equal(1, DurabilityProbeHandler.TimesHandled(probe.Id));
    }

    private static ApiFactory CreateHost(string connectionString) =>
        new ApiFactory(connectionString).WithServices(services => services.ConfigureWolverine(options =>
        {
            options.Discovery.IncludeAssembly(typeof(DurabilityProbeHandler).Assembly);
            options.PublishMessage<DurabilityProbe>().ToLocalQueue(ProbeQueue).UseDurableInbox();
        }));
}

/// <summary>Mensagem de teste (IT-02).</summary>
public sealed record DurabilityProbe(Guid Id);

/// <summary>Handler de teste: conta quantas vezes cada mensagem foi processada.</summary>
public static class DurabilityProbeHandler
{
    private static readonly ConcurrentDictionary<Guid, int> Handled = new();

    public static int TimesHandled(Guid id) => Handled.GetValueOrDefault(id);

    public static void Handle(DurabilityProbe message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Handled.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
    }
}
