using Microsoft.Extensions.DependencyInjection;
using Vitrine.IntegrationTests.Identity.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.IntegrationEvents;
using Vitrine.MercadoLivre.PublicApi;
using Wolverine;

namespace Vitrine.IntegrationTests.Listings.Support;

/// <summary>Conta do ML conhecida pelo Listings (o evento <c>MercadoLivreAccountConnected</c> já foi processado).</summary>
internal sealed record ListingsAccount(Guid AccountId, Guid WorkspaceId, long MlUserId);

/// <summary>
/// Host da Api para os testes do módulo Listings (SPEC-0022): o do Identity (cadastro aberto, assinantes de teste
/// descobertos pelo Wolverine) com <see cref="IMercadoLivreItems"/> trocado pelo <see cref="MercadoLivreItemsDouble"/>
/// (seção 7.3: dublê do contrato SPEC-0021@1). Os eventos consumidos são publicados pelo <see cref="IMessageBus"/> do
/// host, como o outbox do módulo MercadoLivre faria.
/// </summary>
internal static class ListingsHost
{
    private static long _nextMlUserId = 3_300_000_000;

    public static ApiFactory Create(string connectionString, MercadoLivreItemsDouble items) =>
        IdentityHost.Create(connectionString)
            .WithServices(services => services.AddSingleton<IMercadoLivreItems>(items));

    /// <summary>Publica a mensagem pelo Wolverine do host (filas locais duráveis).</summary>
    public static async Task PublishAsync(ApiFactory factory, object message)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(message);
    }

    /// <summary>
    /// Conecta uma conta nova ao workspace: coloca <paramref name="items"/> no catálogo do dublê, publica
    /// <c>MercadoLivreAccountConnected</c> e espera a importação concluir (<c>idle</c>).
    /// </summary>
    public static async Task<ListingsAccount> ConnectAsync(
        ApiFactory factory, MercadoLivreItemsDouble gateway, Guid workspaceId, CancellationToken cancellationToken, params MercadoLivreItem[] items)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(gateway);
        var account = new ListingsAccount(Guid.NewGuid(), workspaceId, Interlocked.Increment(ref _nextMlUserId));
        gateway.Put(account.AccountId, items);
        await PublishAsync(factory, Connected(account));
        await ListingsDatabase.WaitForIdleAsync(factory.ConnectionString, account.AccountId, after: null, cancellationToken);
        return account;
    }

    /// <summary>Nova conta (ainda não publicada) com <paramref name="items"/> no catálogo do dublê.</summary>
    public static ListingsAccount NewAccount(MercadoLivreItemsDouble gateway, Guid workspaceId, params MercadoLivreItem[] items)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var account = new ListingsAccount(Guid.NewGuid(), workspaceId, Interlocked.Increment(ref _nextMlUserId));
        gateway.Put(account.AccountId, items);
        return account;
    }

    public static MercadoLivreAccountConnected Connected(ListingsAccount account) =>
        new(account.AccountId, account.WorkspaceId, account.MlUserId, "MLB", Reconnected: false, DateTimeOffset.UtcNow);

    public static MercadoLivreAccountDisconnected Disconnected(ListingsAccount account) =>
        new(account.AccountId, account.WorkspaceId, account.MlUserId, DateTimeOffset.UtcNow);

    public static MercadoLivreAccountReauthorizationRequired ReauthorizationRequired(ListingsAccount account) =>
        new(account.AccountId, account.WorkspaceId, account.MlUserId, DateTimeOffset.UtcNow);

    public static MercadoLivreItemChanged ItemChanged(ListingsAccount account, string itemId) =>
        new(account.AccountId, account.WorkspaceId, account.MlUserId, itemId, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}
