using Vitrine.MercadoLivre.Domain.Accounts;

namespace Vitrine.UnitTests.MercadoLivre;

public sealed class MercadoLivreAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    // Id acima de Int32 (épico SPEC-0019: long em todo lugar).
    private const long MlUserId = 3_000_000_001;

    [Fact]
    [Trait("spec", "SPEC-0020:UT-02")]
    public void Connect_cria_conta_conectada_com_expiracao_do_access_token_e_evento_nao_reconectado()
    {
        // Dado uma conta nova do ML
        var workspaceId = Guid.NewGuid();
        var seller = new MercadoLivreSeller(MlUserId, "LOJA_TESTE", "MLB");
        var tokens = new MercadoLivreTokens("APP_USR-access-falso-1", "TG-refresh-falso-1", TimeSpan.FromSeconds(21600));

        // Quando conectada
        var account = MercadoLivreAccount.Connect(workspaceId, seller, tokens, Now);

        // Então fica connected, com os dados do vendedor, os tokens e accessTokenExpiresAt = agora + expires_in
        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal(workspaceId, account.WorkspaceId);
        Assert.Equal(MlUserId, account.MlUserId);
        Assert.Equal("LOJA_TESTE", account.Nickname);
        Assert.Equal("MLB", account.SiteId);
        Assert.Equal(MercadoLivreAccountStatus.Connected, account.Status);
        Assert.Equal(tokens.AccessToken, account.AccessToken);
        Assert.Equal(tokens.RefreshToken, account.RefreshToken);
        Assert.Equal(Now.AddSeconds(21600), account.AccessTokenExpiresAt);
        Assert.Equal(Now, account.ConnectedAt);
        Assert.Equal(Now, account.UpdatedAt);

        // E o evento de domínio indica conexão nova (reconnected = false)
        var connected = Assert.IsType<MercadoLivreAccountConnectedDomainEvent>(Assert.Single(account.DomainEvents));
        Assert.Equal(new MercadoLivreAccountConnectedDomainEvent(account.Id, workspaceId, MlUserId, "MLB", Reconnected: false, Now), connected);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-02")]
    public void Reconnect_troca_os_tokens_mantem_connected_recalcula_a_expiracao_e_indica_reconexao()
    {
        // Dado uma conta conectada, com os eventos já publicados
        var workspaceId = Guid.NewGuid();
        var account = MercadoLivreAccount.Connect(
            workspaceId,
            new MercadoLivreSeller(MlUserId, "LOJA_TESTE", "MLB"),
            new MercadoLivreTokens("APP_USR-access-falso-1", "TG-refresh-falso-1", TimeSpan.FromSeconds(21600)),
            Now);
        var accountId = account.Id;
        account.ClearDomainEvents();

        // Quando reconectada mais tarde com tokens novos (e apelido trocado no ML)
        var later = Now.AddDays(3);
        var tokens = new MercadoLivreTokens("APP_USR-access-falso-2", "TG-refresh-falso-2", TimeSpan.FromSeconds(10800));
        account.Reconnect(new MercadoLivreSeller(MlUserId, "LOJA_TESTE_NOVA", "MLB"), tokens, later);

        // Então a mesma conta fica connected com os tokens novos e accessTokenExpiresAt = agora + expires_in
        Assert.Equal(accountId, account.Id);
        Assert.Equal(MercadoLivreAccountStatus.Connected, account.Status);
        Assert.Equal(tokens.AccessToken, account.AccessToken);
        Assert.Equal(tokens.RefreshToken, account.RefreshToken);
        Assert.Equal(later.AddSeconds(10800), account.AccessTokenExpiresAt);
        Assert.Equal("LOJA_TESTE_NOVA", account.Nickname);
        Assert.Equal(later, account.UpdatedAt);

        // E o evento de domínio indica reconexão (reconnected = true)
        var connected = Assert.IsType<MercadoLivreAccountConnectedDomainEvent>(Assert.Single(account.DomainEvents));
        Assert.Equal(new MercadoLivreAccountConnectedDomainEvent(accountId, workspaceId, MlUserId, "MLB", Reconnected: true, later), connected);
    }
}
