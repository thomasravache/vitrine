using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Domain.Accounts;
using Vitrine.MercadoLivre.Infrastructure;
using Vitrine.MercadoLivre.Infrastructure.Persistence;
using Vitrine.MercadoLivre.PublicApi;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>Conta semeada direto no banco (tokens cifrados pela persistência do módulo).</summary>
internal sealed record SeededAccount(Guid AccountId, Guid WorkspaceId, MlSeller Seller, MlTokens Tokens, DateTimeOffset ExpiresAt);

/// <summary>
/// Suporte aos testes do gateway autenticado (SPEC-0021): semeia contas conectadas pelo <c>DbContext</c> do módulo
/// (tokens cifrados com o <c>IFieldProtector</c> do host), lê a conta de volta e chama <see cref="IMercadoLivreItems"/>
/// num escopo de DI próprio, como um módulo consumidor faria.
/// </summary>
internal static class MercadoLivreGateway
{
    /// <summary>Opções nomeadas do pipeline padrão do cliente <c>mercadolivre-api</c> (<c>AddStandardResilienceHandler</c>).</summary>
    public const string ApiResilienceOptionsName = MercadoLivreModule.ApiHttpClientName + "-standard";

    /// <summary>
    /// Conta conectada a um workspace novo, com <paramref name="tokens"/> e access token expirando em
    /// <paramref name="expiresAt"/>.
    /// </summary>
    public static async Task<SeededAccount> SeedAccountAsync(
        ApiFactory factory, MlSeller seller, MlTokens tokens, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(tokens);

        var workspaceId = Guid.NewGuid();
        var lifetime = TimeSpan.FromSeconds(tokens.ExpiresIn);
        var account = MercadoLivreAccount.Connect(
            workspaceId,
            new MercadoLivreSeller(seller.Id, seller.Nickname, seller.SiteId),
            new MercadoLivreTokens(tokens.AccessToken, tokens.RefreshToken, lifetime),
            expiresAt - lifetime);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MercadoLivreModuleDbContext>();
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return new SeededAccount(account.Id, workspaceId, seller, tokens, account.AccessTokenExpiresAt);
    }

    /// <summary>Conta com token válido por mais 6 h.</summary>
    public static Task<SeededAccount> SeedValidAccountAsync(ApiFactory factory, CancellationToken cancellationToken) =>
        SeedAccountAsync(factory, MlSeller.New(), MlTokens.New(), DateTimeOffset.UtcNow.AddHours(6), cancellationToken);

    /// <summary>A conta como o módulo a lê (tokens decifrados), ou nula se não existe.</summary>
    public static async Task<MercadoLivreAccount?> ReadAccountAsync(ApiFactory factory, Guid accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MercadoLivreModuleDbContext>();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.Id == accountId, cancellationToken);
    }

    /// <summary><see cref="IMercadoLivreItems.GetItemsAsync"/> num escopo de DI próprio.</summary>
    public static async Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(
        ApiFactory factory, Guid accountId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var items = scope.ServiceProvider.GetRequiredService<IMercadoLivreItems>();
        return await items.GetItemsAsync(accountId, itemIds, cancellationToken);
    }

    /// <summary>Todas as páginas de <see cref="IMercadoLivreItems.SearchItemIdsAsync"/>, num escopo de DI próprio.</summary>
    public static async Task<List<IReadOnlyList<string>>> SearchItemIdsAsync(
        ApiFactory factory, Guid accountId, string status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var items = scope.ServiceProvider.GetRequiredService<IMercadoLivreItems>();
        var pages = new List<IReadOnlyList<string>>();
        await foreach (var page in items.SearchItemIdsAsync(accountId, status, cancellationToken))
        {
            pages.Add(page);
        }

        return pages;
    }

    /// <summary>
    /// Ajusta o pipeline padrão do cliente <c>mercadolivre-api</c> para o teste: espera entre tentativas curta, fixa e
    /// sem jitter (só o <c>Retry-After</c> faz o teste esperar) e, se pedido, um circuito que abre com poucas chamadas.
    /// O número de tentativas e os timeouts continuam os do módulo.
    /// </summary>
    public static ApiFactory WithFastResilience(this ApiFactory factory, int? circuitMinimumThroughput = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.WithServices(services => services.PostConfigure<HttpStandardResilienceOptions>(ApiResilienceOptionsName, options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(50);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
            if (circuitMinimumThroughput is { } throughput)
            {
                options.CircuitBreaker.MinimumThroughput = throughput;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
            }
        }));
    }
}
