using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vitrine.IntegrationTests.Support;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

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

        // O schema do Wolverine é criado por um host completo, que sobe e para antes de qualquer mensagem.
        await using (var setup = CreateHost(connectionString, recoverPersistedMessages: true))
        {
            _ = setup.Services;
        }

        // Dado o host da Api com Wolverine persistindo no PostgreSQL, sem agente de recuperação neste primeiro host;
        // quando a mensagem é publicada pelo outbox (transação confirmada) e o host para sem entregá-la
        await using (var first = CreateHost(connectionString, recoverPersistedMessages: false))
        {
            using var scope = first.Services.CreateScope();
            var bus = scope.ServiceProvider.GetService<IMessageBus>();
            Assert.True(bus is not null, "Wolverine não está configurado no host da Api (IMessageBus não registrado).");
            var runtime = first.Services.GetRequiredService<IWolverineRuntime>();
            var database = Assert.IsAssignableFrom<IMessageDatabase>(runtime.Storage);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var context = Assert.IsAssignableFrom<MessageBus>(bus);
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, transaction));
            await context.PublishAsync(probe);
            await transaction.CommitAsync(cancellationToken);
            // Sem FlushOutgoingMessagesAsync: a mensagem existe só no banco quando o host para.

            var persisted = await runtime.Storage.Admin.FetchCountsAsync();
            Assert.True(persisted.Incoming > 0, "A mensagem publicada não foi persistida no PostgreSQL.");
        }

        Assert.Equal(0, DurabilityProbeHandler.TimesHandled(probe.Id));

        // E um host novo sobe sobre o mesmo banco
        await using var second = CreateHost(connectionString, recoverPersistedMessages: true);
        var secondRuntime = second.Services.GetRequiredService<IWolverineRuntime>();

        // Então o handler processa a mensagem exatamente uma vez
        await Eventually.TrueAsync(
            () => DurabilityProbeHandler.TimesHandled(probe.Id) > 0,
            TimeSpan.FromSeconds(60),
            "a mensagem persistida não foi processada após o reinício do host",
            cancellationToken);
        await Eventually.TrueAsync(
            async () => (await secondRuntime.Storage.Admin.FetchCountsAsync()).Incoming == 0,
            TimeSpan.FromSeconds(60),
            "a mensagem processada continua pendente no PostgreSQL (poderia ser reprocessada)",
            cancellationToken);
        Assert.Equal(1, DurabilityProbeHandler.TimesHandled(probe.Id));
    }

    private static ApiFactory CreateHost(string connectionString, bool recoverPersistedMessages) =>
        new ApiFactory(connectionString).WithServices(services => services.ConfigureWolverine(options =>
        {
            options.Discovery.IncludeAssembly(typeof(DurabilityProbeHandler).Assembly);
            options.PublishMessage<DurabilityProbe>().ToLocalQueue(ProbeQueue).UseDurableInbox();
            options.Durability.DurabilityAgentEnabled = recoverPersistedMessages;
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
