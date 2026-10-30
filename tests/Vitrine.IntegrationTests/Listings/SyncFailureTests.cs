using Vitrine.IntegrationTests.Listings.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.Listings.Application.Sync;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.Listings;

/// <summary>
/// SPEC-0022:IT-10 — falhas do ML na sincronização: <c>Unavailable</c> deixa <c>failed</c> com
/// <c>lastError=ML_UNAVAILABLE</c> e a mensagem reagendada (retentativa com espera de 1 min, durável);
/// <c>ReauthorizationRequired</c> deixa <c>paused</c>, sem reagendamento. Os anúncios anteriores ficam intactos.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SyncFailureTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("spec", "SPEC-0022:IT-10")]
    public async Task Unavailable_no_meio_da_importacao_deixa_failed_com_ML_UNAVAILABLE_anuncios_intactos_e_mensagem_reagendada()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var closed = ListingItem.New("closed");
        var account = await ListingsHost.ConnectAsync(
            factory, gateway, owner.WorkspaceId, cancellationToken, ListingItem.New("active"), ListingItem.New("active"), ListingItem.New("paused"), closed);
        var before = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);

        // Dado o dublê lançando MercadoLivreUnavailableException na busca de paused (meio da importação), com o encerrado
        // já fora do ML (se a varredura rodasse, ele sumiria)
        gateway.Remove(account.AccountId, closed.Id);
        gateway.FailSearch(account.AccountId, "paused", new MercadoLivreUnavailableException(account.AccountId, "retries esgotados"));
        var startedAt = DateTimeOffset.UtcNow;

        // Quando a sincronização roda
        await ListingsHost.PublishAsync(factory, new SyncListings(account.AccountId, SyncTriggers.Reconciliation));

        // Então sync_states fica failed com lastError=ML_UNAVAILABLE
        var state = await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, account.AccountId, state => state.Status == "failed", "a sincronização não ficou failed", cancellationToken);
        Assert.Equal("ML_UNAVAILABLE", state.LastError);

        // E os anúncios anteriores ficam todos (nenhuma varredura)
        var after = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.Equal(before.Select(row => row.ItemId), after.Select(row => row.ItemId));

        // E a mensagem é reagendada no armazenamento durável (1ª espera: 1 min), sem ir para as mensagens mortas
        List<EnvelopeRow> envelopes = [];
        await Eventually.TrueAsync(
            async () =>
            {
                envelopes = await ListingsDatabase.ReadEnvelopesAsync(connectionString, nameof(SyncListings), account.AccountId, cancellationToken);
                return envelopes.Any(envelope => envelope.Status == "Scheduled");
            },
            TimeSpan.FromSeconds(30),
            "SyncListings não foi reagendado (envelope Scheduled) depois do Unavailable",
            cancellationToken);
        Assert.DoesNotContain(envelopes, envelope => envelope.Status == "DeadLetter");
        var scheduled = Assert.Single(envelopes, envelope => envelope.Status == "Scheduled");
        Assert.NotNull(scheduled.ExecutionTime);
        Assert.InRange(scheduled.ExecutionTime.Value, startedAt.AddSeconds(40), DateTimeOffset.UtcNow.AddSeconds(80));
    }

    [Fact]
    [Trait("spec", "SPEC-0022:IT-10")]
    public async Task ReauthorizationRequired_deixa_paused_sem_reagendamento_e_com_os_anuncios_mantidos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        var gateway = new MercadoLivreItemsDouble();
        await using var factory = ListingsHost.Create(connectionString, gateway);
        using var api = new ListingsApi(factory);
        var owner = await api.SignUpAsync(cancellationToken);
        var active = ListingItem.New("active");
        var account = await ListingsHost.ConnectAsync(factory, gateway, owner.WorkspaceId, cancellationToken, active, ListingItem.New("closed"));
        var before = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);

        // Dado o dublê lançando MercadoLivreReauthorizationRequiredException na busca de closed
        gateway.Remove(account.AccountId, active.Id);
        gateway.FailSearch(account.AccountId, "closed", new MercadoLivreReauthorizationRequiredException(account.AccountId));

        // Quando a sincronização roda
        await ListingsHost.PublishAsync(factory, new SyncListings(account.AccountId, SyncTriggers.Reconciliation));

        // Então sync_states fica paused
        await ListingsDatabase.WaitForSyncStateAsync(
            connectionString, account.AccountId, state => state.Status == "paused", "a sincronização não ficou paused", cancellationToken);

        // E os anúncios ficam todos (inclusive o que sumiu do ML: não houve varredura)
        var after = await ListingsDatabase.ReadListingsAsync(connectionString, account.AccountId, cancellationToken);
        Assert.Equal(before.Select(row => row.ItemId), after.Select(row => row.ItemId));

        // E nada é reagendado: nenhum envelope pendente de SyncListings da conta
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var envelopes = await ListingsDatabase.ReadEnvelopesAsync(connectionString, nameof(SyncListings), account.AccountId, cancellationToken);
        Assert.DoesNotContain(envelopes, envelope => envelope.Status == "Scheduled");
        Assert.DoesNotContain(envelopes, envelope => envelope.Table == "wolverine_incoming_envelopes" && envelope.Status == "Incoming");
    }
}
